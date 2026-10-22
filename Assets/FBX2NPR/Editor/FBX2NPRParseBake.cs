// FBX2NPRParseBake.cs — 얼굴 파싱 라벨을 텍스처(UV) 공간으로 옮긴다 (2026-10-08).
//   1) 머리를 여러 방향에서 찍어 얼굴 파싱(FaceParser)으로 화소별 클래스 확률을 얻고,
//   2) 같은 카메라로 '월드 위치' 데이터 패스를 따로 그려(ParseWorldPos.shader, DrawMeshNow) 무엇이 보이는지 알아낸 뒤,
//   3) 텍스처의 텍셀마다(UV 래스터화로 얻은 월드 위치·법선) 각 뷰로 역투영해 보이는 뷰의 확률을 코사인 가중 평균하고 argmax 한다.
// 결과는 텍셀별 라벨(0~18, 255 = 어느 뷰에서도 안 보임), 덮임(가중치 합), 마진(1등−2등). 안 보인 텍셀은 이웃 라벨로 메우되 '메움' 표시를 남긴다.
// 프로젝트 에셋은 바꾸지 않는다. 호출하는 쪽이 씬에 원본 인스턴스를 세워 두고 넘긴다(비교 렌더와 같은 조명이면 된다).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRParseBake
    {
        const float FovY = 24f;
        const int DataRes = 1024;                 // 월드 위치 데이터 패스 해상도 (파싱 입력 512 보다 촘촘하게 가림을 판정)
        const float CosMin = 0.20f;               // 이보다 스치는 각은 안 쓴다
        const int FillPasses = 12;                // 안 보인 텍셀을 이웃 라벨로 메우는 반복 횟수(텍셀)
        const int ParseLayer = 31;                // 파싱 렌더 전용 레이어: 씬에 같이 있는 다른 물체(변환 중인 인스턴스 등)가 찍히지 않게
        const float AmbientLinear = 0.30f;        // 비교 렌더와 같은 조명 (FBX2NPRCompare)

        public class View { public string name; public float az, el, weight, margin = 1.25f; }
        public static readonly View[] DefaultViews = {
            new View { name = "front",   az = 0f,   el = 0f,   weight = 1.0f },
            new View { name = "left35",  az = 35f,  el = 0f,   weight = 0.8f },
            new View { name = "right35", az = -35f, el = 0f,   weight = 0.8f },
            new View { name = "left70",  az = 70f,  el = 0f,   weight = 0.45f },
            new View { name = "right70", az = -70f, el = 0f,   weight = 0.45f },
            new View { name = "below20", az = 0f,   el = -20f, weight = 0.7f },
            new View { name = "above20", az = 0f,   el = 20f,  weight = 0.6f },
        };

        public class TexResult
        {
            public Texture albedo; public int W, H;         // 라벨 맵 크기 (알베도보다 작을 수 있다)
            public byte[] label;                            // 0~18, 255 = 안 보임(메우지도 못함)
            public bool[] has, filled;                      // 메시가 덮는 텍셀 / 이웃으로 메운 텍셀
            public float[] cover, margin;
            public int hasCount, observed, filledCount;
            public int[] classCount = new int[FaceParser.K];
            public List<string> meshes = new List<string>();
        }
        public class Result
        {
            public Bounds head; public int headVerts;
            public List<TexResult> textures = new List<TexResult>();
            public List<string> log = new List<string>();
            public float alignMismatch = -1f;               // 색 렌더와 데이터 패스의 실루엣 불일치 비율(정면). 커지면 투영이 어긋난 것
        }

        class Slot { public string name; public Mesh mesh; public Matrix4x4 l2w; public int sub; public bool cutout; public float cutoff; public Texture albedo; }
        class ViewData
        {
            public View view; public Vector3 camPos; public Matrix4x4 vp; public float[] probs; public float[] data; public float pmaxMean;
            public float fpAtUnit;                         // 거리 1 에서의 화소 발자국(m/px, 데이터 해상도 기준)
        }

        static string F(double v, string fmt) { return v.ToString(fmt, CultureInfo.InvariantCulture); }

        /// <summary>머리 본 자체의 가중치가 0.5 이상인 정점의 월드 바운드(BakeMesh, 지금 자세). 머리 본에 묶이지 않은 정적 얼굴 메시(유니티짱)는 그 근처의 렌더러 바운드로 보탠다.</summary>
        public static bool HeadBounds(GameObject root, FBX2NPRAssetSettings crit, out Bounds bounds, out int count)
        {
            bounds = new Bounds(); count = 0;
            var headBone = FBX2NPRConverter.FindHeadBone(root.transform, crit.headBoneContains);
            if (headBone == null) return false;
            bool any = false;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = smr.sharedMesh; if (mesh == null || !smr.gameObject.activeInHierarchy) continue;
                if (crit.IsIgnoredMesh(smr.name)) continue;
                // 머리 본 자체의 가중치만 센다. 자식 본까지 세면 유니티짱의 무릎까지 오는 머리카락(머리 본 밑의 머리카락 본)이 들어와 전신이 찍힌다(실측).
                var bones = smr.bones; var isHead = new bool[bones.Length];
                for (int i = 0; i < bones.Length; i++) isHead[i] = bones[i] != null && bones[i] == headBone;
                var bw = mesh.boneWeights; if (bw.Length != mesh.vertexCount) continue;
                var bm = new Mesh(); smr.BakeMesh(bm, true); var v = bm.vertices;
                for (int i = 0; i < v.Length; i++)
                {
                    float w = 0f; var b = bw[i];
                    if (b.boneIndex0 < isHead.Length && isHead[b.boneIndex0]) w += b.weight0;
                    if (b.boneIndex1 < isHead.Length && isHead[b.boneIndex1]) w += b.weight1;
                    if (b.boneIndex2 < isHead.Length && isHead[b.boneIndex2]) w += b.weight2;
                    if (b.boneIndex3 < isHead.Length && isHead[b.boneIndex3]) w += b.weight3;
                    if (w < 0.5f) continue;
                    var p = smr.transform.TransformPoint(v[i]);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; } else bounds.Encapsulate(p);
                    count++;
                }
                UnityEngine.Object.DestroyImmediate(bm);
            }
            if (!any) return false;
            var b0 = bounds; var near = new Bounds(b0.center, b0.size * 1.5f);
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var r = mf.GetComponent<Renderer>(); if (r == null || mf.sharedMesh == null || !mf.gameObject.activeInHierarchy) continue;
                if (crit.IsIgnoredMesh(mf.name)) continue;
                var rb = r.bounds;
                if (near.Contains(rb.center) && rb.size.magnitude <= b0.size.magnitude) { bounds.Encapsulate(rb); count += mf.sharedMesh.vertexCount; }
            }
            return true;
        }

        /// <param name="root">씬에 세워 둔 원본 인스턴스 (FixOriginal 적용 후)</param>
        /// <param name="labelRes">라벨 맵 한 변의 상한 (알베도가 더 작으면 알베도 크기)</param>
        /// <param name="debugDir">null 이 아니면 뷰별 렌더·라벨 맵 PNG 를 남긴다</param>
        public static Result Bake(GameObject root, FBX2NPRAssetSettings crit, string modelPath, FaceParser parser, int labelRes, View[] views, string debugDir, string tag)
        {
            var res = new Result(); var log = res.log;
            if (views == null) views = DefaultViews;
            if (!HeadBounds(root, crit, out res.head, out res.headVerts)) { log.Add("머리 본을 못 찾음 (headBoneContains='" + crit.headBoneContains + "')"); return res; }
            log.Add(string.Format(CultureInfo.InvariantCulture, "머리 바운드: 정점 {0}개, 중심 ({1:F3}, {2:F3}, {3:F3}) 크기 ({4:F3}, {5:F3}, {6:F3})",
                res.headVerts, res.head.center.x, res.head.center.y, res.head.center.z, res.head.size.x, res.head.size.y, res.head.size.z));

            // 1) 재질 칸(서브메시)을 알베도 텍스처별로 모은다. 스킨 메시는 지금 자세로 굽는다.
            var slots = new List<Slot>(); var temps = new List<UnityEngine.Object>();
            var dataMat = new Material(Shader.Find("FBX2NPR/ParseWorldPos")); dataMat.hideFlags = HideFlags.HideAndDontSave; temps.Add(dataMat);
            // 씬의 다른 물체·조명에 영향받지 않게: 이 인스턴스만 전용 레이어로 찍고, 조명은 비교 렌더와 같은 것을 직접 세운다. 끝나면 되돌린다.
            var layerOf = new Dictionary<Transform, int>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) { layerOf[t] = t.gameObject.layer; t.gameObject.layer = ParseLayer; }
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None); var lightWas = new bool[lights.Length];
            for (int i = 0; i < lights.Length; i++) { lightWas[i] = lights[i].enabled; lights[i].enabled = false; }
            var ambMode = RenderSettings.ambientMode; var ambCol = RenderSettings.ambientLight; var refl = RenderSettings.reflectionIntensity; var sunWas = RenderSettings.sun;
            var sunGo = new GameObject("ParseSun"); sunGo.hideFlags = HideFlags.HideAndDontSave; var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional; sun.intensity = 1f; sun.color = Color.white; sun.shadows = LightShadows.None;
            sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.DefaultLight(), Vector3.up);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(AmbientLinear, AmbientLinear, AmbientLinear, 1f).gamma;   // 색 입력은 감마 값
            RenderSettings.reflectionIntensity = 0f; RenderSettings.sun = sun;
            try
            {
                foreach (var rend in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!rend.gameObject.activeInHierarchy || !rend.enabled) continue;
                    if (crit.IsIgnoredMesh(rend.name)) continue;
                    Mesh mesh = null; Matrix4x4 l2w = rend.transform.localToWorldMatrix;
                    var smr = rend as SkinnedMeshRenderer;
                    if (smr != null) { if (smr.sharedMesh == null) continue; mesh = new Mesh(); smr.BakeMesh(mesh, true); temps.Add(mesh); }
                    else { var mf = rend.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue; mesh = mf.sharedMesh; }
                    var mats = rend.sharedMaterials;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var m = mats.Length > 0 ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                        var alb = FBX2NPRConverter.ResolveAlbedo(m, modelPath, crit);
                        bool cut = m != null && m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f;
                        float cutoff = m != null && m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f;
                        slots.Add(new Slot { name = rend.name, mesh = mesh, l2w = l2w, sub = s, cutout = cut && alb != null, cutoff = cutoff, albedo = alb });
                    }
                }

                // 2) 뷰마다: 색 렌더 → 파싱 확률, 데이터 패스 → 월드 위치
                var vds = new List<ViewData>();
                foreach (var v in views)
                {
                    var vd = RenderView(res.head, v, slots, dataMat, parser, log, debugDir, tag);
                    if (vd != null) vds.Add(vd);
                }
                if (vds.Count == 0) { log.Add("뷰를 하나도 못 찍음"); return res; }
                // 정렬 검사: 정면 뷰의 '배경이 아닌 화소'와 데이터 패스의 '지오메트리 있음'이 얼마나 다른가
                res.alignMismatch = vds[0].probs != null ? AlignCheck(vds[0], log) : -1f;

                // 3) 텍스처별 역투영
                var byTex = new Dictionary<Texture, List<Slot>>();
                foreach (var s in slots) { if (s.albedo == null) continue; List<Slot> l; if (!byTex.TryGetValue(s.albedo, out l)) byTex[s.albedo] = l = new List<Slot>(); l.Add(s); }
                foreach (var kv in byTex)
                {
                    var tr = Project(kv.Key, kv.Value, vds, labelRes, log);
                    if (tr == null) continue;
                    res.textures.Add(tr);
                    if (debugDir != null) SaveDebug(tr, debugDir, tag);
                }
            }
            finally
            {
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                foreach (var kv in layerOf) if (kv.Key != null) kv.Key.gameObject.layer = kv.Value;
                for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].enabled = lightWas[i];
                RenderSettings.ambientMode = ambMode; RenderSettings.ambientLight = ambCol; RenderSettings.reflectionIntensity = refl; RenderSettings.sun = sunWas;
                UnityEngine.Object.DestroyImmediate(sunGo);
            }
            return res;
        }

        // ---------- 뷰 렌더 ----------

        static Camera MakeCamera(Bounds bb, View v, out float dist)
        {
            var camGo = new GameObject("ParseCam"); camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.20f, 0.20f, 0.22f);
            cam.fieldOfView = FovY; cam.aspect = 1f; cam.allowHDR = false; cam.allowMSAA = false; cam.cullingMask = 1 << ParseLayer;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None; acd.dithering = false; acd.renderShadows = false;
            var dir = FBX2NPRConverter.LightDir(v.az, v.el);
            float halfH = Mathf.Max(bb.size.y * 0.5f, bb.size.x * 0.5f);
            dist = halfH / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * v.margin + bb.size.z * 0.5f;
            camGo.transform.position = bb.center + dir.normalized * dist;
            camGo.transform.rotation = Quaternion.LookRotation(bb.center - camGo.transform.position, Vector3.up);
            float r = bb.extents.magnitude;
            cam.nearClipPlane = Mathf.Max(0.01f, dist - r * 3f); cam.farClipPlane = dist + r * 6f;
            return cam;
        }

        static ViewData RenderView(Bounds head, View v, List<Slot> slots, Material dataMat, FaceParser parser, List<string> log, string debugDir, string tag)
        {
            float dist; var cam = MakeCamera(head, v, out dist);
            try
            {
                int N = FaceParser.N;
                // 색 렌더 (URP 카메라) → sRGB 바이트, 행 0 = 위
                var rt = new RenderTexture(N, N, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); rt.Create();
                bool done = false;
                try { var rq = new UniversalRenderPipeline.SingleCameraRequest(); rq.destination = rt;
                      if (RenderPipeline.SupportsRenderRequest(cam, rq)) { cam.SubmitRenderRequest(rq); done = true; } } catch { }
                if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var t = new Texture2D(N, N, TextureFormat.RGBA32, false, false);
                t.ReadPixels(new Rect(0, 0, N, N), 0, 0); t.Apply(); RenderTexture.active = prev;
                var px = t.GetPixels32(); UnityEngine.Object.DestroyImmediate(t); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
                var rgba = new byte[N * N * 4];
                for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) { var p = px[(N - 1 - y) * N + x]; int o = (y * N + x) * 4; rgba[o] = p.r; rgba[o + 1] = p.g; rgba[o + 2] = p.b; rgba[o + 3] = 255; }

                var vd = new ViewData { view = v, camPos = cam.transform.position, vp = cam.projectionMatrix * cam.worldToCameraMatrix };
                vd.fpAtUnit = 2f * Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) / DataRes;
                // 파싱
                var logits = parser.Logits(rgba);
                vd.probs = new float[logits.Length];
                vd.pmaxMean = FaceParser.Softmax(logits, vd.probs);
                // 정면 쪽 뷰(방위각 0)의 입술·입 라벨은 좌우 대칭으로 합친다. 파서가 한쪽 아랫입술을 짧게 잡아(Ch07 오른쪽 아래) 렌더에서 입꼬리 아래가 비었다 (기록 95번 추가 4).
                if (Mathf.Abs(v.az) < 0.5f) SymmetrizeLips(vd.probs, N, log, v.name);
                // 데이터 패스
                vd.data = RenderData(cam, slots, dataMat);
                // 뷰 요약
                var cnt = new int[FaceParser.K]; int P = N * N;
                for (int i = 0; i < P; i++) { int best = 0; float bv = -1f; for (int k = 0; k < FaceParser.K; k++) { float pv = vd.probs[k * P + i]; if (pv > bv) { bv = pv; best = k; } } cnt[best]++; }
                var sb = new StringBuilder();
                sb.Append("  뷰 " + v.name + " (가중치 " + F(v.weight, "F2") + "): 평균 최대확률 " + F(vd.pmaxMean, "F3") + " |");
                var order = new List<int>(); for (int k = 1; k < FaceParser.K; k++) if (cnt[k] > 0) order.Add(k);
                order.Sort((a, b) => cnt[b].CompareTo(cnt[a]));
                int shown = 0; foreach (var k in order) { if (shown++ >= 8) break; sb.Append(" " + FaceParser.Names[k] + " " + F(100.0 * cnt[k] / P, "F1") + "%"); }
                log.Add(sb.ToString());
                if (debugDir != null)
                {
                    SaveRgba(Path.Combine(debugDir, tag + "_view_" + v.name + "_in.png"), rgba, N, N);
                    var lab = new Color32[P]; var ovl = new Color32[P];
                    for (int i = 0; i < P; i++)
                    {
                        int best = 0; float bv = -1f; for (int k = 0; k < FaceParser.K; k++) { float pv = vd.probs[k * P + i]; if (pv > bv) { bv = pv; best = k; } }
                        int y = i / N, x = i % N, o = (N - 1 - y) * N + x; var lc = FaceParser.Palette[best];
                        lab[o] = lc; ovl[o] = new Color32((byte)((rgba[i * 4] + lc.r) / 2), (byte)((rgba[i * 4 + 1] + lc.g) / 2), (byte)((rgba[i * 4 + 2] + lc.b) / 2), 255);
                    }
                    SavePng(Path.Combine(debugDir, tag + "_view_" + v.name + "_lab.png"), lab, N, N);
                    SavePng(Path.Combine(debugDir, tag + "_view_" + v.name + "_ovl.png"), ovl, N, N);
                }
                return vd;
            }
            catch (Exception ex) { log.Add("  뷰 " + v.name + " 실패: " + ex.Message); return null; }
            finally { UnityEngine.Object.DestroyImmediate(cam.gameObject); }
        }

        /// <summary>같은 카메라로 월드 위치 데이터 패스. 반환: DataRes² × 4 float (행 0 = 아래), a = 1 이면 지오메트리.</summary>
        static float[] RenderData(Camera cam, List<Slot> slots, Material dataMat)
        {
            var rt = new RenderTexture(DataRes, DataRes, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear); rt.Create();
            var prev = RenderTexture.active;
            Graphics.SetRenderTarget(rt);
            GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
            GL.PushMatrix();
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;
            foreach (var s in slots)
            {
                dataMat.SetFloat("_AlphaClip", s.cutout ? 1f : 0f); dataMat.SetFloat("_Cutoff", s.cutoff);
                dataMat.SetTexture("_BaseMap", s.cutout ? s.albedo : null);
                if (!dataMat.SetPass(0)) continue;
                Graphics.DrawMeshNow(s.mesh, s.l2w, s.sub);
            }
            GL.PopMatrix();
            var t = new Texture2D(DataRes, DataRes, TextureFormat.RGBAFloat, false, true);
            t.ReadPixels(new Rect(0, 0, DataRes, DataRes), 0, 0); t.Apply();
            RenderTexture.active = prev;
            var cols = t.GetPixelData<float>(0);                 // RGBAFloat: 4 float/화소, 행 0 = 아래
            var data = new float[cols.Length]; cols.CopyTo(data);
            UnityEngine.Object.DestroyImmediate(t); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
            return data;
        }

        /// <summary>색 렌더의 '배경 아님'(확률 argmax ≠ bg) 과 데이터 패스의 '지오메트리 있음' 실루엣을 비교. 뒤집힘·어긋남 검출용.</summary>
        static float AlignCheck(ViewData vd, List<string> log)
        {
            int N = FaceParser.N, P = N * N; int mism = 0, mismFlip = 0;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                int i = y * N + x;   // probs: 행 0 = 위
                int best = 0; float bv = -1f; for (int k = 0; k < FaceParser.K; k++) { float pv = vd.probs[k * P + i]; if (pv > bv) { bv = pv; best = k; } }
                bool fg = best != FaceParser.Bg;
                int dx = x * DataRes / N, dyTop = (N - 1 - y) * DataRes / N, dyFlip = y * DataRes / N;   // data: 행 0 = 아래
                bool geo = vd.data[(dyTop * DataRes + dx) * 4 + 3] > 0.5f;
                bool geoF = vd.data[(dyFlip * DataRes + dx) * 4 + 3] > 0.5f;
                if (fg != geo) mism++; if (fg != geoF) mismFlip++;
            }
            float m = (float)mism / P, mf = (float)mismFlip / P;
            log.Add("  정렬 검사(정면): 파싱 전경 대 데이터 패스 실루엣 불일치 " + F(100f * m, "F1") + "% (상하 뒤집어 비교하면 " + F(100f * mf, "F1") + "%)" + (mf < m ? " — 데이터 패스가 뒤집혀 있다!" : ""));
            return m;
        }

        // ---------- 역투영 ----------

        static TexResult Project(Texture albedo, List<Slot> slots, List<ViewData> vds, int labelRes, List<string> log)
        {
            int W = Mathf.Min(labelRes, albedo.width); int H = Mathf.Max(1, Mathf.RoundToInt((float)W * albedo.height / albedo.width));
            int P = W * H, K = FaceParser.K;
            var pos = new Vector3[P]; var nrm = new Vector3[P]; var has = new bool[P]; var tri = new byte[P];
            // UV 래스터화 (RegionFlatBake 와 같은 규칙: 타일 밖 UV 는 삼각형 단위로 한 칸씩 당김)
            foreach (var s in slots)
            {
                var v = s.mesh.vertices; var n = s.mesh.normals; var uv = s.mesh.uv; var idx = s.mesh.GetTriangles(s.sub);
                if (uv == null || uv.Length != v.Length) continue;
                bool hasN = n != null && n.Length == v.Length;
                for (int t = 0; t < idx.Length; t += 3)
                {
                    int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                    Vector2 a = uv[i0], b = uv[i1], c = uv[i2];
                    var off = new Vector2(Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), Mathf.Floor(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                    a -= off; b -= off; c -= off;
                    if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) > 1.0001f || Mathf.Max(a.y, Mathf.Max(b.y, c.y)) > 1.0001f) continue;
                    float ax = a.x * W, ay = a.y * H, bx = b.x * W, by = b.y * H, cx = c.x * W, cy = c.y * H;
                    float det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                    if (Mathf.Abs(det) < 1e-6f) continue;
                    var P0 = s.l2w.MultiplyPoint3x4(v[i0]); var P1 = s.l2w.MultiplyPoint3x4(v[i1]); var P2 = s.l2w.MultiplyPoint3x4(v[i2]);
                    Vector3 N0, N1, N2;
                    if (hasN) { N0 = s.l2w.MultiplyVector(n[i0]).normalized; N1 = s.l2w.MultiplyVector(n[i1]).normalized; N2 = s.l2w.MultiplyVector(n[i2]).normalized; }
                    else { var fn = Vector3.Cross(P1 - P0, P2 - P0).normalized; N0 = N1 = N2 = fn; }
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx)))), x1 = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(ax, Mathf.Max(bx, cx))));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy)))), y1 = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(ay, Mathf.Max(by, cy))));
                    float inv = 1f / det;
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                    {
                        float qx = x + 0.5f, qy = y + 0.5f;
                        float l1 = ((qx - ax) * (cy - ay) - (cx - ax) * (qy - ay)) * inv;
                        float l2 = ((bx - ax) * (qy - ay) - (qx - ax) * (by - ay)) * inv;
                        float l0 = 1f - l1 - l2;
                        if (l0 < -1e-4f || l1 < -1e-4f || l2 < -1e-4f) continue;
                        int i = y * W + x;
                        pos[i] = P0 * l0 + P1 * l1 + P2 * l2; nrm[i] = (N0 * l0 + N1 * l1 + N2 * l2).normalized;
                        has[i] = true; if (tri[i] < 255) tri[i]++;
                    }
                }
            }
            int hasCount = 0; foreach (var h in has) if (h) hasCount++;
            if (hasCount == 0) return null;

            // 뷰별 누적
            var acc = new float[K * P]; var wsum = new float[P]; int N = FaceParser.N; int NP = N * N;
            float wMax = 0f; foreach (var vd in vds) wMax += vd.view.weight;
            var seenByView = new int[vds.Count];
            for (int vi = 0; vi < vds.Count; vi++)
            {
                var vd = vds[vi]; var M = vd.vp; var data = vd.data; var probs = vd.probs; float vw = vd.view.weight;
                for (int i = 0; i < P; i++)
                {
                    if (!has[i]) continue;
                    var p = pos[i];
                    float cw = M.m30 * p.x + M.m31 * p.y + M.m32 * p.z + M.m33; if (cw <= 1e-6f) continue;
                    float nx = (M.m00 * p.x + M.m01 * p.y + M.m02 * p.z + M.m03) / cw;
                    float ny = (M.m10 * p.x + M.m11 * p.y + M.m12 * p.z + M.m13) / cw;
                    if (nx < -1f || nx > 1f || ny < -1f || ny > 1f) continue;
                    int dx = Mathf.Clamp((int)((nx + 1f) * 0.5f * DataRes), 0, DataRes - 1), dy = Mathf.Clamp((int)((ny + 1f) * 0.5f * DataRes), 0, DataRes - 1);
                    int di = (dy * DataRes + dx) * 4;
                    if (data[di + 3] < 0.5f) continue;
                    var toCam = vd.camPos - p; float dist = toCam.magnitude; if (dist < 1e-5f) continue;
                    float cos = Vector3.Dot(nrm[i], toCam / dist); if (cos < CosMin) continue;
                    float ddx = data[di] - p.x, ddy = data[di + 1] - p.y, ddz = data[di + 2] - p.z;
                    float eps = Mathf.Max(0.002f, 2.5f * vd.fpAtUnit * dist / Mathf.Max(cos, 0.3f));
                    if (ddx * ddx + ddy * ddy + ddz * ddz > eps * eps) continue;                 // 다른 표면이 앞에 있다
                    int px = Mathf.Clamp((int)((nx + 1f) * 0.5f * N), 0, N - 1), pyTop = Mathf.Clamp(N - 1 - (int)((ny + 1f) * 0.5f * N), 0, N - 1);
                    int pi = pyTop * N + px;
                    float w = vw * cos * cos;
                    for (int k = 0; k < K; k++) acc[k * P + i] += w * probs[k * NP + pi];
                    wsum[i] += w; seenByView[vi]++;
                }
            }

            var tr = new TexResult { albedo = albedo, W = W, H = H, label = new byte[P], has = has, filled = new bool[P], cover = new float[P], margin = new float[P], hasCount = hasCount };
            foreach (var s in slots) if (!tr.meshes.Contains(s.name)) tr.meshes.Add(s.name);
            for (int i = 0; i < P; i++)
            {
                if (!has[i] || wsum[i] <= 0f) { tr.label[i] = 255; continue; }
                int best = 0; float b1 = -1f, b2 = -1f;
                for (int k = 0; k < K; k++) { float a = acc[k * P + i]; if (a > b1) { b2 = b1; b1 = a; best = k; } else if (a > b2) b2 = a; }
                tr.label[i] = (byte)best; tr.cover[i] = Mathf.Min(1f, wsum[i] / wMax); tr.margin[i] = (b1 - Mathf.Max(0f, b2)) / wsum[i];
                tr.observed++; tr.classCount[best]++;
            }
            // 안 보인 텍셀을 이웃 라벨로 메운다 (관측된 라벨만 퍼진다. 덮임은 0 으로 남겨 '메움'을 구분)
            var cur = (byte[])tr.label.Clone();
            for (int pass = 0; pass < FillPasses; pass++)
            {
                var next = (byte[])cur.Clone(); bool changed = false;
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    int i = y * W + x; if (!has[i] || cur[i] != 255) continue;
                    int bestK = -1; int bestN = 0; var cnt = new int[K];
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        int j = yy * W + xx; if (!has[j] || cur[j] == 255) continue;
                        if (++cnt[cur[j]] > bestN) { bestN = cnt[cur[j]]; bestK = cur[j]; }
                    }
                    if (bestK >= 0) { next[i] = (byte)bestK; tr.filled[i] = true; changed = true; }
                }
                cur = next; if (!changed) break;
            }
            tr.label = cur; foreach (var f in tr.filled) if (f) tr.filledCount++;

            var sb = new StringBuilder();
            sb.Append("  텍스처 " + albedo.name + " (" + W + "x" + H + ", 메시 " + string.Join("/", tr.meshes.ToArray()) + "): 덮는 텍셀 " + hasCount
                + ", 관측 " + F(100.0 * tr.observed / hasCount, "F1") + "%, 이웃으로 메움 " + F(100.0 * tr.filledCount / hasCount, "F1") + "%, 못 메움 " + F(100.0 * CountEq(tr.label, has, 255) / hasCount, "F1") + "% |");
            var order = new List<int>(); for (int k = 0; k < K; k++) if (tr.classCount[k] > 0) order.Add(k);
            order.Sort((a, b) => tr.classCount[b].CompareTo(tr.classCount[a]));
            foreach (var k in order) sb.Append(" " + FaceParser.Names[k] + " " + F(100.0 * tr.classCount[k] / Mathf.Max(1, tr.observed), "F1") + "%");
            sb.Append(" | 뷰별 관측 텍셀:"); for (int vi = 0; vi < vds.Count; vi++) sb.Append(" " + vds[vi].view.name + " " + seenByView[vi]);
            log.Add(sb.ToString());
            return tr;
        }

        static int CountEq(byte[] a, bool[] mask, byte v) { int n = 0; for (int i = 0; i < a.Length; i++) if (mask[i] && a[i] == v) n++; return n; }

        // ---------- 디버그 저장 ----------

        static void SaveDebug(TexResult tr, string dir, string tag)
        {
            int W = tr.W, H = tr.H, P = W * H;
            var alb = RegionFlatBake.Read(tr.albedo, W, H);                     // 행 0 = 아래 (라벨 맵과 같은 방향)
            var lab = new Color32[P]; var ovl = new Color32[P]; var cov = new Color32[P];
            for (int i = 0; i < P; i++)
            {
                Color32 lc;
                if (!tr.has[i]) lc = new Color32(0, 0, 0, 255);
                else if (tr.label[i] == 255) lc = new Color32(50, 50, 50, 255);
                else { var c = FaceParser.Palette[tr.label[i]]; lc = tr.filled[i] ? new Color32((byte)(c.r * 0.55f), (byte)(c.g * 0.55f), (byte)(c.b * 0.55f), 255) : c; }
                lab[i] = lc;
                var a = alb[i];
                ovl[i] = tr.has[i] && tr.label[i] != 255 ? new Color32((byte)((a.r + lc.r) / 2), (byte)((a.g + lc.g) / 2), (byte)((a.b + lc.b) / 2), 255) : a;
                byte cv = (byte)Mathf.RoundToInt(255f * tr.cover[i]); byte mg = (byte)Mathf.RoundToInt(255f * Mathf.Clamp01(tr.margin[i]));
                cov[i] = new Color32(cv, mg, tr.has[i] ? (byte)60 : (byte)0, 255);
            }
            string b = Path.Combine(dir, tag + "_" + Safe(tr.albedo.name));
            SavePng(b + "_label.png", lab, W, H); SavePng(b + "_label_over.png", ovl, W, H); SavePng(b + "_cover.png", cov, W, H);
        }

        static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }
        static void SavePng(string file, Color32[] px, int W, int H)
        {
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false, false); t.SetPixels32(px); t.Apply();
            File.WriteAllBytes(file, t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
        }
        /// <summary>입술(윗·아랫·입 안) 확률을 얼굴 축(코 확률의 무게중심 x, 없으면 가운데)에 대해 좌우 최대값으로 합친다. 올린 만큼 나머지 클래스를 줄여 합을 1 로 둔다.</summary>
        static void SymmetrizeLips(float[] probs, int N, List<string> log, string viewName)
        {
            int P = N * N; int[] lips = { FaceParser.Mouth, FaceParser.ULip, FaceParser.LLip };
            double sx = 0, sw = 0; for (int i = 0; i < P; i++) { float pn = probs[FaceParser.Nose * P + i]; if (pn > 0.5f) { sx += (i % N) * pn; sw += pn; } }
            float axis = sw > 0 ? (float)(sx / sw) : 0.5f * (N - 1);
            var buf = new float[P * 3]; for (int k = 0; k < 3; k++) Array.Copy(probs, lips[k] * P, buf, k * P, P);
            int changed = 0;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                int xm = Mathf.RoundToInt(2f * axis - x); if (xm < 0 || xm >= N) continue;
                int i = y * N + x, j = y * N + xm; float before = 0f, after = 0f; bool inc = false;
                for (int k = 0; k < 3; k++) { float a = buf[k * P + i], b = buf[k * P + j]; before += a; if (b > a) { probs[lips[k] * P + i] = b; after += b; inc = true; } else after += a; }
                if (!inc) continue;
                float rest = 1f - before, restNew = 1f - after;
                if (rest > 1e-6f && restNew >= 0f) { float sc = restNew / rest; for (int k = 0; k < FaceParser.K; k++) if (k != lips[0] && k != lips[1] && k != lips[2]) probs[k * P + i] *= sc; }
                changed++;
            }
            log.Add("  뷰 " + viewName + ": 입술 라벨 좌우 대칭 합치기 — 축 x=" + F(axis, "F1") + ", 올린 화소 " + changed);
        }

        static void SaveRgba(string file, byte[] rgbaTopDown, int W, int H)
        {
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { int i = (y * W + x) * 4; px[(H - 1 - y) * W + x] = new Color32(rgbaTopDown[i], rgbaTopDown[i + 1], rgbaTopDown[i + 2], 255); }
            SavePng(file, px, W, H);
        }
    }

    /// <summary>배치 시험: 4체를 원본 상태로 세워 라벨 맵을 굽고 NormalTestOut/parsebake/ 에 남긴다. rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRParseBakeTest.Run</summary>
    public static class FBX2NPRParseBakeTest
    {
        const float AmbientLinear = 0.30f;
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "parsebake"); } }

        public static void Run()
        {
            if (!Application.isBatchMode && !UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var log = new List<string>(); Directory.CreateDirectory(OutDir);
            var temps = new List<UnityEngine.Object>(); FaceParser parser = null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                parser = FaceParser.Load(log); if (parser == null) return;
                var only = Environment.GetEnvironmentVariable("FBX2NPR_ONLY");
                foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
                {
                    var modelPath = AssetDatabase.GUIDToAssetPath(g);
                    if (!modelPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                    string c = Path.GetFileNameWithoutExtension(modelPath);
                    if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(';'), c) < 0) continue;
                    log.Add(""); log.Add("==================== " + c);
                    UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(AmbientLinear, AmbientLinear, AmbientLinear, 1f).gamma;
                    RenderSettings.reflectionIntensity = 0f;
                    var sunGo = new GameObject("Sun"); var sun = sunGo.AddComponent<Light>();
                    sun.type = LightType.Directional; sun.intensity = 1f; sun.color = Color.white; sun.shadows = LightShadows.None;
                    sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.DefaultLight(), Vector3.up);
                    RenderSettings.sun = sun;
                    var imp = AssetImporter.GetAtPath(modelPath);
                    var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
                    var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
                    foreach (var note in FBX2NPRCompare.FixOriginal(pbr, crit, modelPath, temps)) log.Add("  " + note);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = FBX2NPRParseBake.Bake(pbr, crit, modelPath, parser, 1024, null, OutDir, c);
                    sw.Stop();
                    foreach (var l in r.log) log.Add(l);
                    log.Add("  소요 " + sw.ElapsedMilliseconds + "ms, 텍스처 " + r.textures.Count + "개");
                    UnityEngine.Object.DestroyImmediate(pbr);
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                if (parser != null) parser.Dispose();
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                File.WriteAllText(Path.Combine(OutDir, "log_parsebake.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[ParseBake] " + l);
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            }
        }
    }
}
