using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.RuntimeCheck
{
    /// <summary>
    /// 게임 적용 검증 드라이버. 플레이 모드(에디터)와 빌드된 플레이어 둘 다에서 같은 일을 한다.
    /// 애니메이션 상태(Animator 클립)마다, 클립이 없으면 걷기 흉내(ProceduralWalker)로 한 번:
    ///   1) 정지 10프레임 → 프레임끼리 화소가 같은가 (렌더가 결정적인가, 리그가 매 프레임 흔들지 않는가)
    ///   2) 재생 중 6프레임 간격으로 8장 캡처 (시트용) + 60프레임 평균 프레임 시간
    ///   3) 느린 재생(0.15배) 연속 8프레임 → 이웃 프레임 평균차와 밝기가 40 넘게 뛴 화소 수 (외곽선·명암 떨림 지표)
    /// 끝에 모바일 품질 단계(외곽선 피처가 없는 렌더러)로 한 장. 프레임 간격은 Time.captureFramerate 로 1/60초 고정 — 실행마다 같은 프레임에 같은 자세.
    /// 얼굴 카메라는 머리 본을 따라간다(위치만, 방향은 고정). 캡처는 LateUpdate(리그 뒤, 실행 순서 20000)에서 카메라를 RenderTexture 로 직접 렌더한다.
    /// 결과 PNG 와 runtime_log.txt 는 -captureDir [경로] 또는 persistentDataPath/toonkit_runtime. 외곽선 피처는 -featureMode toon|paint 로 어느 리그 값을 넣을지 정한다.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    public class RuntimeCheckDriver : MonoBehaviour
    {
        public Camera[] cams; public string[] camNames; public int[] camW, camH;
        [Tooltip("카메라마다 따라갈 본 (넓은 카메라는 비움). 시작할 때의 상대 위치를 유지한다.")]
        public Transform[] follow;
        public ProceduralWalker[] walkers;
        public Animator[] animators; public string[] states;
        public ToonFaceRig[] toonRigs; public PainterlyRig[] paintRigs;
        public bool quitWhenDone = true;

        string dir, featureMode; readonly List<string> log = new List<string>();
        RenderTexture[] rts; Texture2D[] txs; Vector3[] followOffset;
        class Req { public int cam; public string file; public byte[] data; }
        readonly List<Req> queue = new List<Req>();
        SobelOutlineFeature feat;

        static string Arg(string key)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == key) return a[i + 1];
            return null;
        }
        static string Safe(string s) { var sb = new StringBuilder(); foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_'); return sb.ToString(); }

        void Start()
        {
            dir = Arg("-captureDir") ?? Path.Combine(Application.persistentDataPath, "toonkit_runtime");
            featureMode = Arg("-featureMode") ?? Environment.GetEnvironmentVariable("FBX2NPR_RC_FEATURE") ?? "toon";
            Directory.CreateDirectory(dir);
            Application.runInBackground = true; Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            Time.captureFramerate = 60;
            // 수동 캡처는 LateUpdate 에서 렌더하는데, Unity 는 스킨 메시 변형을 그 뒤(PostLateUpdate)에 갱신한다 → 일부 메시가 이전 프레임 자세로 찍혀
            // 신발이 발에서 떨어지고 머리카락이 늦게 따라오는 것처럼 보였다(실제 화면 출력은 정상 — 기록 97번). 렌더할 때마다 현재 본으로 다시 계산하게 한다 (검증용 캡처에만)
            int smrN = 0; foreach (var smr in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { smr.forceMatrixRecalculationPerRender = true; smrN++; }
            L("캡처 보정: 스킨 메시 " + smrN + "개에 forceMatrixRecalculationPerRender");
            // 발표용 큰 캡처: -wideSize 1920x1080 이면 넓은 카메라(0번)만 그 크기로 찍는다 (같은 16:9 면 구도는 같다, 기록 98번)
            var ws = Arg("-wideSize");
            if (!string.IsNullOrEmpty(ws))
            {
                var wp = ws.ToLowerInvariant().Split('x'); int ww, wh;
                if (wp.Length == 2 && int.TryParse(wp[0], out ww) && int.TryParse(wp[1], out wh) && ww > 0 && wh > 0) { camW[0] = ww; camH[0] = wh; L("넓은 카메라 캡처 크기: " + ww + "x" + wh + " (-wideSize)"); }
            }
            rts = new RenderTexture[cams.Length]; txs = new Texture2D[cams.Length]; followOffset = new Vector3[cams.Length];
            for (int i = 0; i < cams.Length; i++)
            {
                rts[i] = new RenderTexture(camW[i], camH[i], 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); rts[i].Create();
                txs[i] = new Texture2D(camW[i], camH[i], TextureFormat.RGBA32, false, false);
                if (follow != null && i < follow.Length && follow[i] != null) followOffset[i] = cams[i].transform.position - follow[i].position;
            }
            L("환경: " + (Application.isEditor ? "에디터 플레이 모드" : "플레이어 빌드") + ", Unity " + Application.unityVersion + ", " + SystemInfo.graphicsDeviceType + " " + SystemInfo.graphicsDeviceName + ", 배치 " + Application.isBatchMode + ", 화면 " + Screen.width + "x" + Screen.height);
            var rp = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
            L("품질 단계: " + QualitySettings.names[QualitySettings.GetQualityLevel()] + ", RP 에셋 " + (rp != null ? rp.name : "없음"));
            L("외곽선 셰이더 Shader.Find(FBX2NPR/SobelOutline): " + (Shader.Find("FBX2NPR/SobelOutline") != null ? "있음" : "없음 — 빌드에 포함되지 않음"));
            L("캡처 폴더: " + dir + ", 카메라 " + cams.Length + "대, featureMode=" + featureMode);
            if (animators != null && animators.Length > 0)
                foreach (var a in animators) if (a != null) L("Animator " + a.name + ": 아바타 " + (a.avatar != null ? a.avatar.name + " (사람형 " + a.avatar.isHuman + ", 유효 " + a.avatar.isValid + ")" : "없음") + ", 컨트롤러 " + (a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "없음") + ", 루트 모션 " + a.applyRootMotion);
            foreach (var w in walkers) if (w != null) L("걷기 흉내 " + w.name + ": " + w.Found());
            feat = FindFeat();
            L("외곽선 피처 (씬 로드 직후 = 마지막 OnEnable 리그의 값): " + FeatState());
            if (featureMode == "paint") { foreach (var r in paintRigs) if (r != null) { r.yieldToToon = false; r.ApplyInk(); break; } }   // 시험용: 툰 리그가 있어도 회화풍 값을 강제로
            else { foreach (var r in toonRigs) if (r != null) { r.ApplyOutline(); break; } }
            // 진단용: -featureOff 1 이면 외곽선 피처를 끈 채로 찍는다 (어두운 곳이 선 때문인지 명암 때문인지 가르는 대조). 리그는 OnEnable 에서만 피처를 켜므로 여기서 끄면 유지된다
            if (Arg("-featureOff") == "1" && feat != null) feat.SetActive(false);
            // 진단용: -sunBias 깊이,노멀 이면 해의 그림자 보정값을 파이프라인 설정 대신 이 값으로 찍는다 (그림자가 발에서 떨어져 보이는 것이 보정값 탓인지 가르는 대조)
            var sb = Arg("-sunBias");
            if (!string.IsNullOrEmpty(sb) && RenderSettings.sun != null)
            {
                var sp = sb.Split(','); float sd, sn;
                if (sp.Length == 2 && float.TryParse(sp[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sd)
                                   && float.TryParse(sp[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sn))
                {
                    var ld = RenderSettings.sun.GetComponent<UniversalAdditionalLightData>();
                    if (ld == null) ld = RenderSettings.sun.gameObject.AddComponent<UniversalAdditionalLightData>();
                    ld.usePipelineSettings = false; RenderSettings.sun.shadowBias = sd; RenderSettings.sun.shadowNormalBias = sn;
                    L("해 그림자 보정값 (-sunBias): 깊이 " + sd + ", 노멀 " + sn + " (파이프라인 설정 대신)");
                }
            }
            contactLog = Arg("-contactLog") == "1";
            L("외곽선 피처 (featureMode=" + featureMode + " 적용 후): " + FeatState());
            foreach (var r in paintRigs) if (r != null) L("회화풍 안쪽 선 지우기(시작, 다음 LateUpdate 에 반영) " + r.transform.root.name + ": " + r.DebugFade());
            CollectChars();
            csv.Add("state,character,state_ok,normalized_time_delta,hand_move_m,foot_move_m,rect_w,rect_h,overlap_pct,static_max_px,slow_mean_avg,slow_mean_max,slow_jumps_max,rect_x,rect_y_bottom");
            StartCoroutine(Run());
        }

        // ---- 캐릭터별 지표 (결과표용, 기록 98번): 넓은 화면에서 캐릭터 영역만 잘라 정지·느린 재생 차이, 손·발 이동, 재생 중인 상태
        GameObject[] chars; readonly List<string> csv = new List<string>();

        void CollectChars()
        {
            var list = new List<GameObject>();
            if (animators != null) foreach (var a in animators) if (a != null) list.Add(a.gameObject);
            if (list.Count == 0 && walkers != null) foreach (var w in walkers) if (w != null) list.Add(w.gameObject);
            chars = list.ToArray();
        }

        /// <summary>넓은 카메라(0번) 화면에서 캐릭터가 차지하는 사각형 (ReadPixels 데이터처럼 아래가 0). 스킨 메시의 bounds 는 T자세 기준이라 이웃과 겹치므로 지금 자세로 구운 정점을 투영한다.</summary>
        RectInt CharRect(int k)
        {
            var cam = cams[0]; int W = camW[0], H = camH[0];
            // 카메라의 WorldToViewportPoint 는 화면(배치 에디터는 640x480, 4:3) 비율로 투영해서, 16:9 렌더 텍스처 캡처와 어긋나 양 끝 캐릭터가 잘렸다 (기록 98번). 캡처 비율로 직접 투영한다.
            var vp = Matrix4x4.Perspective(cam.fieldOfView, (float)W / H, cam.nearClipPlane, cam.farClipPlane) * cam.worldToCameraMatrix;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue; bool any = false;
            var bm = new Mesh();
            foreach (var r in chars[k].GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null)
                {
                    if (smr.sharedMesh == null) continue;
                    smr.BakeMesh(bm, true); var v = bm.vertices; var tr = smr.transform; int step = Math.Max(1, v.Length / 6000);
                    for (int i = 0; i < v.Length; i += step)
                    {
                        var wp = tr.TransformPoint(v[i]); var c4 = vp * new Vector4(wp.x, wp.y, wp.z, 1f); if (c4.w <= 1e-6f) continue;
                        float px = (c4.x / c4.w * 0.5f + 0.5f) * W, py = (c4.y / c4.w * 0.5f + 0.5f) * H;
                        x0 = Mathf.Min(x0, px); x1 = Mathf.Max(x1, px); y0 = Mathf.Min(y0, py); y1 = Mathf.Max(y1, py); any = true;
                    }
                }
                else
                {
                    var b = r.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var c4 = vp * new Vector4((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z, 1f); if (c4.w <= 1e-6f) continue;
                        float px = (c4.x / c4.w * 0.5f + 0.5f) * W, py = (c4.y / c4.w * 0.5f + 0.5f) * H;
                        x0 = Mathf.Min(x0, px); x1 = Mathf.Max(x1, px); y0 = Mathf.Min(y0, py); y1 = Mathf.Max(y1, py); any = true;
                    }
                }
            }
            Destroy(bm);
            if (!any) return new RectInt(0, 0, 0, 0);
            int ix0 = Mathf.Clamp(Mathf.FloorToInt(x0) - 3, 0, W), iy0 = Mathf.Clamp(Mathf.FloorToInt(y0) - 3, 0, H);
            int ix1 = Mathf.Clamp(Mathf.CeilToInt(x1) + 3, 0, W), iy1 = Mathf.Clamp(Mathf.CeilToInt(y1) + 3, 0, H);
            return new RectInt(ix0, iy0, Math.Max(0, ix1 - ix0), Math.Max(0, iy1 - iy0));
        }
        RectInt[] CharRects() { var o = new RectInt[chars.Length]; for (int k = 0; k < chars.Length; k++) o[k] = CharRect(k); return o; }
        static RectInt Union(RectInt a, RectInt b)
        {
            if (a.width <= 0 || a.height <= 0) return b; if (b.width <= 0 || b.height <= 0) return a;
            int x0 = Math.Min(a.xMin, b.xMin), y0 = Math.Min(a.yMin, b.yMin), x1 = Math.Max(a.xMax, b.xMax), y1 = Math.Max(a.yMax, b.yMax);
            return new RectInt(x0, y0, x1 - x0, y1 - y0);
        }
        /// <summary>이 사각형이 다른 캐릭터 사각형과 겹치는 넓이 (%) — 겹친 부분의 차이는 이웃 몫도 섞인다</summary>
        static float OverlapPct(RectInt[] rs, int k)
        {
            var a = rs[k]; long area = (long)a.width * a.height; if (area <= 0) return 0f;
            var cover = new bool[a.width * a.height]; long n = 0;
            for (int j = 0; j < rs.Length; j++)
            {
                if (j == k) continue; var b = rs[j];
                int x0 = Math.Max(a.xMin, b.xMin), x1 = Math.Min(a.xMax, b.xMax), y0 = Math.Max(a.yMin, b.yMin), y1 = Math.Min(a.yMax, b.yMax);
                for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { int i = (y - a.yMin) * a.width + (x - a.xMin); if (!cover[i]) { cover[i] = true; n++; } }
            }
            return 100f * n / area;
        }
        static int DifferRect(byte[] a, byte[] b, int W, RectInt r, int tol)
        {
            int n = 0;
            for (int y = r.yMin; y < r.yMax; y++) for (int x = r.xMin; x < r.xMax; x++)
            {
                int i = (y * W + x) * 4; if (Math.Abs(a[i] - b[i]) > tol || Math.Abs(a[i + 1] - b[i + 1]) > tol || Math.Abs(a[i + 2] - b[i + 2]) > tol) n++;
            }
            return n;
        }
        static double MeanRect(byte[] a, byte[] b, int W, RectInt r, int tol, out int jumps)
        {
            long sum = 0; jumps = 0; long cnt = 0;
            for (int y = r.yMin; y < r.yMax; y++) for (int x = r.xMin; x < r.xMax; x++)
            {
                int i = (y * W + x) * 4;
                int la = (a[i] * 54 + a[i + 1] * 183 + a[i + 2] * 19) >> 8, lb = (b[i] * 54 + b[i + 1] * 183 + b[i + 2] * 19) >> 8;
                int d = Math.Abs(la - lb); sum += d; cnt++; if (d > tol) jumps++;
            }
            return cnt > 0 ? (double)sum / cnt : 0.0;
        }
        static readonly HumanBodyBones[] Hands = { HumanBodyBones.LeftHand, HumanBodyBones.RightHand }, Feet = { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot };
        /// <summary>손·발 본의 캐릭터 루트 기준 위치 (Humanoid 가 아니면 빈 배열)</summary>
        static Vector3[] LimbPose(GameObject go)
        {
            var an = go.GetComponent<Animator>(); if (an == null || an.avatar == null || !an.avatar.isHuman) return new Vector3[0];
            var o = new Vector3[4]; int k = 0;
            foreach (var hb in Hands) { var t = an.GetBoneTransform(hb); o[k++] = t != null ? go.transform.InverseTransformPoint(t.position) : Vector3.zero; }
            foreach (var hb in Feet) { var t = an.GetBoneTransform(hb); o[k++] = t != null ? go.transform.InverseTransformPoint(t.position) : Vector3.zero; }
            return o;
        }

        SobelOutlineFeature FindFeat()
        {
            foreach (var r in toonRigs) { var f = r != null ? r.outlineFeature as SobelOutlineFeature : null; if (f != null) return f; }
            foreach (var r in paintRigs) { var f = r != null ? r.outlineFeature as SobelOutlineFeature : null; if (f != null) return f; }
            return null;
        }
        string FeatState()
        {
            if (feat == null) return "없음";
            var s = feat.settings;
            return string.Format("active={0} darkness={1:F2} thickness={2:F2} depth={3:F1} normal={4:F2} threshold={5:F2}", feat.isActive, s.darkness, s.thickness, s.depthScale, s.normalScale, s.threshold);
        }

        void L(string s) { log.Add(s); Debug.Log("[RuntimeCheck] " + s); }

        Req[] Capture(string tag)
        {
            var rs = new Req[cams.Length];
            for (int i = 0; i < cams.Length; i++) { rs[i] = new Req { cam = i, file = tag != null ? Path.Combine(dir, tag + "_" + camNames[i] + ".png") : null }; queue.Add(rs[i]); }
            return rs;
        }
        static byte[][] Data(Req[] rs) { var o = new byte[rs.Length][]; for (int i = 0; i < rs.Length; i++) o[i] = rs[i].data; return o; }

        // 진단용(-contactLog 1): 캡처하는 프레임마다 캐릭터별 가장 낮은 정점과 두 발 본의 높이(바닥 y=0 기준)를 기록한다. 그림자가 떠 보일 때 발이 실제로 떠 있는지 가른다
        bool contactLog; readonly List<string> contactCsv = new List<string>();
        void LogContact(string tag)
        {
            if (contactCsv.Count == 0) contactCsv.Add("frame,character,min_vertex_y,left_foot_y,right_foot_y,left_toes_y,right_toes_y");
            var bm = new Mesh();
            foreach (var go in chars)
            {
                if (go == null) continue;
                float minY = float.MaxValue;
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!smr.enabled || !smr.gameObject.activeInHierarchy || smr.sharedMesh == null) continue;
                    smr.BakeMesh(bm, true); var v = bm.vertices; var tr = smr.transform;
                    for (int i = 0; i < v.Length; i++) { float y = tr.TransformPoint(v[i]).y; if (y < minY) minY = y; }
                }
                var an = go.GetComponent<Animator>();
                System.Func<HumanBodyBones, string> by = hb => { var t = (an != null && an.isHuman) ? an.GetBoneTransform(hb) : null; return t != null ? t.position.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : ""; };
                contactCsv.Add(string.Join(",", new[] { tag, go.name, minY.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                    by(HumanBodyBones.LeftFoot), by(HumanBodyBones.RightFoot), by(HumanBodyBones.LeftToes), by(HumanBodyBones.RightToes) }));
            }
            Destroy(bm);
        }

        void LateUpdate()
        {
            if (queue.Count == 0) return;
            if (contactLog) { string tag = queue[0].file != null ? Path.GetFileNameWithoutExtension(queue[0].file) : "f" + Time.frameCount; LogContact(tag); }
            for (int i = 0; i < cams.Length; i++) if (follow != null && i < follow.Length && follow[i] != null) cams[i].transform.position = follow[i].position + followOffset[i];
            foreach (var r in queue) r.data = Grab(r.cam, r.file);
            queue.Clear();
        }

        IEnumerator Run()
        {
            foreach (var r in paintRigs) if (r != null && r.HasEyeInk) L("눈매 잉크 진단(시작) " + r.transform.root.name + ": " + r.DebugInk());
            for (int i = 0; i < 15; i++) yield return null;      // 셰이더 컴파일·첫 프레임 안정화
            foreach (var r in paintRigs) if (r != null && r.HasEyeInk) L("눈매 잉크 진단(15프레임) " + r.transform.root.name + ": " + r.DebugInk());
            foreach (var r in paintRigs) if (r != null) L("회화풍 안쪽 선 지우기(15프레임) " + r.transform.root.name + ": " + r.DebugFade());
            { int hidden = 0; var names = new List<string>(); foreach (var r in paintRigs) if (r != null) foreach (var rr in r.transform.root.GetComponentsInChildren<Renderer>(true)) if (!rr.enabled) { hidden++; names.Add(r.transform.root.name + "/" + rr.name); } L("회화풍 프리팹의 꺼진 렌더러 " + hidden + "개" + (hidden > 0 ? ": " + string.Join(", ", names.ToArray()) : "")); }
            // 포트폴리오용 GIF: -gifState <상태> 이면 검증 대신 그 상태 한 사이클을 같은 간격으로 연속 캡처하고 끝낸다 (-gifStep 프레임 간격, 기본 2 = 30fps)
            var gifState = Arg("-gifState");
            if (!string.IsNullOrEmpty(gifState))
            {
                yield return GifRun(gifState);
                File.WriteAllText(Path.Combine(dir, "runtime_log.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                if (contactCsv.Count > 0) File.WriteAllText(Path.Combine(dir, "contact.csv"), string.Join("\n", contactCsv.ToArray()), new UTF8Encoding(true));
                if (quitWhenDone)
                {
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.ExitPlaymode();
#else
                    Application.Quit();
#endif
                }
                yield break;
            }
            int rec0 = SobelOutlineFeature.s_RecordCalls;
            bool anim = animators != null && animators.Length > 0 && states != null && states.Length > 0;
            if (anim) { foreach (var st in states) yield return Phase(st); }
            else yield return Phase(null);
            L("외곽선 패스 기록 횟수 (전체): " + (SobelOutlineFeature.s_RecordCalls - rec0) + " (0 이면 패스가 안 돌았다)");
            // 모바일 품질 단계
            int q = QualitySettings.GetQualityLevel(), qm = Array.IndexOf(QualitySettings.names, "Mobile");
            if (qm < 0 && QualitySettings.names.Length > 1) qm = q == 0 ? 1 : 0;   // 이름이 다르면 현재와 다른 첫 단계
            L("품질 단계 목록: " + string.Join("|", QualitySettings.names) + ", 현재 " + q + ", 모바일 " + qm);
            if (qm >= 0 && qm != q)
            {
                bool ok = true;
                try { QualitySettings.SetQualityLevel(qm, true); } catch (Exception e) { ok = false; L("모바일 품질 전환 실패: " + e.Message); }
                if (ok)
                {
                    for (int k = 0; k < 3; k++) yield return null;
                    var rp2 = QualitySettings.renderPipeline != null ? QualitySettings.renderPipeline : GraphicsSettings.defaultRenderPipeline;
                    L("모바일 품질 단계로 전환: " + QualitySettings.names[QualitySettings.GetQualityLevel()] + ", RP 에셋 " + (rp2 != null ? rp2.name : "없음") + " — 이 렌더러에는 외곽선 피처가 없다");
                    Capture("mobile"); yield return null;
                    QualitySettings.SetQualityLevel(q, true);
                    for (int k = 0; k < 2; k++) yield return null;
                }
            }
            L("끝: 총 " + Time.frameCount + "프레임, " + Time.realtimeSinceStartup.ToString("F1") + "초");
            File.WriteAllText(Path.Combine(dir, "runtime_log.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "per_character.csv"), string.Join("\n", csv.ToArray()), new UTF8Encoding(true));
            if (quitWhenDone)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.ExitPlaymode();
#else
                Application.Quit();
#endif
            }
        }

        /// <summary>한 상태를 한 사이클(클립 길이)만큼 일정한 간격으로 연속 캡처 — 처음과 끝이 이어지는 GIF 재료. 파일 gif_00_wide.png ...</summary>
        IEnumerator GifRun(string state)
        {
            int stepFrames; if (!int.TryParse(Arg("-gifStep"), out stepFrames) || stepFrames <= 0) stepFrames = 2;
            int h = Animator.StringToHash(state); int ok = 0;
            for (int i = 0; i < animators.Length; i++)
            {
                var a = animators[i]; if (a == null || !a.HasState(0, h)) continue;
                a.speed = 1f; a.Play(h, 0, (i * 0.17f) % 1f); ok++;   // 캐릭터마다 다른 위상 (검증과 같은 값)
            }
            SetSpeed(1f);
            for (int i = 0; i < 12; i++) yield return null;
            float len = 1f; foreach (var a in animators) if (a != null && a.HasState(0, h)) { len = a.GetCurrentAnimatorStateInfo(0).length; break; }
            int n = Mathf.Max(2, Mathf.RoundToInt(len * 60f / stepFrames));
            L("GIF 캡처: 상태 " + state + " (Animator " + ok + "개), 클립 길이 " + len.ToString("F4") + "초, " + n + "장, 간격 " + stepFrames + "프레임(60fps 고정)");
            for (int i = 0; i < n; i++) { Capture("gif_" + i.ToString("D2")); for (int k = 0; k < stepFrames; k++) yield return null; }
            L("GIF 캡처 끝: 총 " + Time.frameCount + "프레임");
        }

        /// <summary>한 애니메이션 상태(또는 걷기 흉내)에 대한 정지·재생·느린 재생. 파일 이름 앞에 상태 이름을 붙인다.</summary>
        IEnumerator Phase(string state)
        {
            string pre = state != null ? Safe(state) + "_" : "";
            string label = state ?? "걷기 흉내";
            if (state != null)
            {
                int bad = 0;
                for (int i = 0; i < animators.Length; i++)
                {
                    var a = animators[i]; if (a == null) continue;
                    int h = Animator.StringToHash(state); if (!a.HasState(0, h)) { bad++; continue; }
                    a.speed = 1f; a.Play(h, 0, (i * 0.17f) % 1f);   // 캐릭터마다 다른 위상
                }
                L("==== 상태 " + state + (bad > 0 ? " (상태가 없는 Animator " + bad + "개)" : ""));
            }
            for (int i = 0; i < 12; i++) yield return null;
            SetSpeed(0f);
            for (int i = 0; i < 3; i++) yield return null;
            var rectS = CharRects();
            // 1) 정지 10프레임
            var stat = new List<byte[][]>();
            for (int i = 0; i < 10; i++) { var r = Capture(i < 2 ? pre + "static_" + i : null); yield return null; stat.Add(Data(r)); }
            for (int c = 0; c < cams.Length; c++)
            {
                int maxDiff = 0; for (int i = 1; i < stat.Count; i++) maxDiff = Math.Max(maxDiff, Differ(stat[0][c], stat[i][c], 8));
                L(string.Format("[{0}] 정지 10프레임 [{1}]: 첫 프레임과 다른 화소(채널 차 8 초과) 최대 {2} / {3}", label, camNames[c], maxDiff, camW[c] * camH[c]));
            }
            var charStatic = new int[chars.Length];
            for (int ci = 0; ci < chars.Length; ci++) { int m = 0; for (int i = 1; i < stat.Count; i++) m = Math.Max(m, DifferRect(stat[0][0], stat[i][0], camW[0], rectS[ci], 8)); charStatic[ci] = m; }
            // 2) 재생: 6프레임 간격 8장 (캐릭터마다 손·발 본이 루트 기준으로 얼마나 움직였나, 재생 중인 상태가 맞나)
            SetSpeed(1f);
            var limb0 = new Vector3[chars.Length][]; var handMove = new float[chars.Length]; var footMove = new float[chars.Length]; var nt0 = new float[chars.Length];
            for (int i = 0; i < 8; i++)
            {
                for (int k = 0; k < 6; k++) yield return null;
                Capture(pre + "walk_" + i);
                if (!Application.isBatchMode) StartCoroutine(ScreenShot(pre + "screen_" + i));   // 같은 프레임의 실제 화면 출력(프레임 끝) — 수동 캡처와 대조용
                for (int ci = 0; ci < chars.Length; ci++)
                {
                    var lp = LimbPose(chars[ci]); if (lp.Length == 0) continue;
                    if (limb0[ci] == null) { limb0[ci] = lp; var an0 = chars[ci].GetComponent<Animator>(); nt0[ci] = an0 != null ? an0.GetCurrentAnimatorStateInfo(0).normalizedTime : 0f; continue; }
                    for (int h = 0; h < 2; h++) handMove[ci] = Mathf.Max(handMove[ci], (lp[h] - limb0[ci][h]).magnitude);
                    for (int f = 2; f < 4; f++) footMove[ci] = Mathf.Max(footMove[ci], (lp[f] - limb0[ci][f]).magnitude);
                }
                yield return null;
            }
            var stateOk = new string[chars.Length]; var ntDelta = new float[chars.Length];
            for (int ci = 0; ci < chars.Length; ci++)
            {
                var an1 = chars[ci].GetComponent<Animator>();
                if (state == null || an1 == null) { stateOk[ci] = "-"; continue; }
                var info = an1.GetCurrentAnimatorStateInfo(0);
                stateOk[ci] = info.shortNameHash == Animator.StringToHash(state) ? "yes" : "no"; ntDelta[ci] = info.normalizedTime - nt0[ci];
            }
            float t0 = Time.realtimeSinceStartup; int f0 = Time.frameCount;
            for (int i = 0; i < 60; i++) yield return null;
            L(string.Format("[{0}] 재생 8장 캡처 (6프레임 간격), 재생 중 프레임 시간 {1:F2} ms/프레임 (캡처 없이 {2}프레임)", label, (Time.realtimeSinceStartup - t0) * 1000f / (Time.frameCount - f0), Time.frameCount - f0));
            // 3) 느린 재생 연속 8프레임
            SetSpeed(0.15f);
            for (int k = 0; k < 3; k++) yield return null;
            var rectA = CharRects();
            var slow = new List<byte[][]>();
            for (int i = 0; i < 8; i++) { var r = Capture(pre + "slow_" + i); yield return null; slow.Add(Data(r)); }
            var rectB = CharRects();
            for (int c = 0; c < cams.Length; c++)
            {
                var sb = new StringBuilder("[" + label + "] 느린 재생 연속 8프레임 [" + camNames[c] + "] 이웃 프레임: ");
                for (int i = 1; i < slow.Count; i++) { double mean; int jump = Jumps(slow[i - 1][c], slow[i][c], 40, out mean); sb.Append(string.Format("{0}→{1} 평균차 {2:F2} 급변 {3}; ", i - 1, i, mean, jump)); }
                L(sb.ToString());
                SaveDiff(slow[3][c], slow[4][c], camW[c], camH[c], Path.Combine(dir, pre + "slow_diff_3_4_" + camNames[c] + ".png"));
            }
            // 캐릭터별 (넓은 화면에서 그 캐릭터 영역만)
            var rectU = new RectInt[chars.Length]; for (int ci = 0; ci < chars.Length; ci++) rectU[ci] = Union(rectA[ci], rectB[ci]);
            for (int ci = 0; ci < chars.Length; ci++)
            {
                double sumMean = 0, maxMean = 0; int maxJump = 0;
                for (int i = 1; i < slow.Count; i++) { int j; double m = MeanRect(slow[i - 1][0], slow[i][0], camW[0], rectU[ci], 40, out j); sumMean += m; maxMean = Math.Max(maxMean, m); maxJump = Math.Max(maxJump, j); }
                double avgMean = sumMean / Math.Max(1, slow.Count - 1);
                float ov = OverlapPct(rectU, ci);
                L(string.Format("[{0}] 캐릭터 {1}: 상태 일치 {2}, 재생 진행 {3:F2}회, 손 최대 이동 {4:F3}m, 발 최대 이동 {5:F3}m | 넓은 화면 영역 {6}x{7}px(이웃과 겹침 {8:F0}%) 정지 10프레임 다른 화소 최대 {9}, 느린 재생 이웃 프레임 평균차 평균 {10:F2}·최대 {11:F2}, 급변(밝기 40 초과) 최대 {12}",
                    label, chars[ci].name, stateOk[ci], ntDelta[ci], handMove[ci], footMove[ci], rectU[ci].width, rectU[ci].height, ov, charStatic[ci], avgMean, maxMean, maxJump));
                csv.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0},{1},{2},{3:F3},{4:F4},{5:F4},{6},{7},{8:F1},{9},{10:F3},{11:F3},{12},{13},{14}",
                    label, chars[ci].name, stateOk[ci], ntDelta[ci], handMove[ci], footMove[ci], rectU[ci].width, rectU[ci].height, ov, charStatic[ci], avgMean, maxMean, maxJump, rectU[ci].x, rectU[ci].y));
            }
        }

        IEnumerator ScreenShot(string tag)
        {
            yield return new WaitForEndOfFrame();
            var t = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, tag + "_wide.png"), t.EncodeToPNG()); Destroy(t);
        }

        void SetSpeed(float s)
        {
            if (walkers != null) foreach (var w in walkers) if (w != null) w.speed = s;
            if (animators != null) foreach (var a in animators) if (a != null) a.speed = s;
        }

        byte[] Grab(int ci, string file)
        {
            var cam = cams[ci]; var rt = rts[ci]; var tx = txs[ci];
            bool ok = false;
            try { var rq = new UniversalRenderPipeline.SingleCameraRequest(); rq.destination = rt; if (RenderPipeline.SupportsRenderRequest(cam, rq)) { cam.SubmitRenderRequest(rq); ok = true; } } catch (Exception e) { L("렌더 요청 실패 [" + camNames[ci] + "]: " + e.Message); }
            if (!ok) { var prevT = cam.targetTexture; cam.targetTexture = rt; cam.Render(); cam.targetTexture = prevT; }
            var prev = RenderTexture.active; RenderTexture.active = rt; tx.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tx.Apply(); RenderTexture.active = prev;
            if (file != null) File.WriteAllBytes(file, tx.EncodeToPNG());
            return tx.GetRawTextureData();
        }

        static int Differ(byte[] a, byte[] b, int tol)
        {
            int n = 0; for (int i = 0; i < a.Length; i += 4) if (Math.Abs(a[i] - b[i]) > tol || Math.Abs(a[i + 1] - b[i + 1]) > tol || Math.Abs(a[i + 2] - b[i + 2]) > tol) n++;
            return n;
        }
        static int Jumps(byte[] a, byte[] b, int tol, out double mean)
        {
            long sum = 0; int jumps = 0;
            for (int i = 0; i < a.Length; i += 4)
            {
                int la = (a[i] * 54 + a[i + 1] * 183 + a[i + 2] * 19) >> 8, lb = (b[i] * 54 + b[i + 1] * 183 + b[i + 2] * 19) >> 8;
                int d = Math.Abs(la - lb); sum += d; if (d > tol) jumps++;
            }
            mean = (double)sum / (a.Length / 4); return jumps;
        }
        void SaveDiff(byte[] a, byte[] b, int W, int H, string file)
        {
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false, false); var px = new Color32[W * H];
            for (int i = 0; i < W * H; i++) { int d = Math.Min(255, 4 * (Math.Abs(a[i * 4] - b[i * 4]) + Math.Abs(a[i * 4 + 1] - b[i * 4 + 1]) + Math.Abs(a[i * 4 + 2] - b[i * 4 + 2]))); px[i] = new Color32((byte)d, (byte)d, (byte)d, 255); }
            t.SetPixels32(px); t.Apply(); File.WriteAllBytes(file, t.EncodeToPNG()); Destroy(t);
        }

        void OnDestroy() { if (rts != null) foreach (var rt in rts) if (rt) rt.Release(); }
    }
}
