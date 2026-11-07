using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 얼굴 규칙을 SDF 맵 초안으로 굽는다. 규칙은 코드에 있어서 고칠 수 없지만, SDF 맵은 디자이너가 고칠 수 있고 맵을 읽는 다른 셰이더에서도 쓸 수 있다.
    ///
    ///   1) 굽기: 빛의 좌우 각도를 5도씩 돌리며, 화면과 같은 규칙(ToonFaceRule.hlsl)을 얼굴 UV 에 그려 각도별 흑백 마스크를 PNG 로 남긴다.
    ///   2) 합치기: 마스크를 맵 한 장으로 합친다 (FaceSdfCombine). 디자이너는 마스크를 고친 뒤 합치기만 다시 돌리면 된다.
    ///
    /// 맵은 얼굴 재질의 텍스처마다 한 장이다. 눈·입이 다른 텍스처인 모델(Galaxia)은 맵도 두 장이 된다. 얼굴 앞면 표면적이 2% 안 되는 텍스처는 규칙으로 둔다.
    /// 마스크는 프리팹 폴더의 FaceSDF~ 에 둔다. 이름 끝의 ~ 때문에 Unity 가 가져오지 않아, 마스크 수백 장이 텍스처로 임포트되지 않는다.
    /// 다시 변환해도 마스크는 남지만, 재질은 새로 만들어지므로 맵을 다시 넣어야 한다 (Apply).
    /// </summary>
    public static class FaceSdfBake
    {
        public const int StepDeg = 5, DefaultSize = 1024;
        const float MinFrontShare = 0.02f;
        const string BakeShader = "Hidden/FBX2NPR/FaceMaskBake";

        public class Report
        {
            public List<string> lines = new List<string>();
            public List<string> maps = new List<string>();   // 만든 맵 에셋 경로
        }

        class Slot { public Mesh mesh; public Matrix4x4 l2w; public int sub; public Material mat; }

        class Group
        {
            public Texture albedo; public string name;
            public List<Slot> slots = new List<Slot>();
            public List<Material> mats = new List<Material>();
            public Rect uv; public bool uvInit; public double frontArea;
        }

        [Serializable] class Info { public int width, height; public float x, y, w, h; public int step; }

        /// <summary>마스크를 두는 폴더. Unity 가 가져오지 않는 폴더다.</summary>
        public static string MaskRoot(string prefabPath) { return Path.GetDirectoryName(prefabPath).Replace('\\', '/') + "/FaceSDF~"; }

        static string MapPath(string prefabPath, string texName) { return Path.GetDirectoryName(prefabPath).Replace('\\', '/') + "/" + texName + "_faceSDF.png"; }

        static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

        static string MaskFile(string dir, string kind, int side, int deg) { return string.Format(CultureInfo.InvariantCulture, "{0}/{1}_{2}_{3:000}.png", dir, kind, side > 0 ? "r" : "l", deg); }

        /// <summary>마스크를 굽고 맵까지 합친다. 재질에 맵을 넣지만 켜지는 않는다 (SetUse).</summary>
        public static Report Bake(string prefabPath, int size = DefaultSize)
        {
            var rep = new Report();
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            var temps = new List<UnityEngine.Object>();
            RenderTexture rt = null; Texture2D read = null;
            try
            {
                var rig = root.GetComponentInChildren<ToonFaceRig>(true);
                if (rig == null) { rep.lines.Add("리그 없음: " + prefabPath); return rep; }
                var shader = Shader.Find(BakeShader);
                if (shader == null) { rep.lines.Add("셰이더 없음: " + BakeShader); return rep; }
                var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; temps.Add(mat);
                var groups = CollectGroups(rig, temps);
                double total = 0; foreach (var g in groups) total += g.frontArea;
                Vector3 up = rig.transform.up, fwd = rig.transform.forward, right = Vector3.Cross(up, fwd).normalized;

                // 리그의 값(코·눈 자리, 턱, 명암선 위치)을 굽기 재질에 넣는다. 화면과 같은 경로다.
                rig.faceMaterials.Add(mat); rig.Apply(); rig.faceMaterials.Remove(mat);

                string maskRoot = MaskRoot(prefabPath);
                foreach (var g in groups)
                {
                    float share = total > 0 ? (float)(g.frontArea / total) : 0f;
                    if (share < MinFrontShare) { rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}: 얼굴 앞면의 {1:P1}라 규칙으로 둠", g.name, share)); continue; }
                    if (!g.uvInit) continue;
                    int W, H;
                    if (g.uv.width >= g.uv.height) { W = size; H = Mathf.Max(8, Mathf.RoundToInt(size * g.uv.height / g.uv.width)); }
                    else { H = size; W = Mathf.Max(8, Mathf.RoundToInt(size * g.uv.width / g.uv.height)); }
                    rt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Point }; rt.Create();
                    read = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
                    string dir = maskRoot + "/" + Safe(g.name);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    Directory.CreateDirectory(dir);
                    mat.SetVector("_BakeUvRect", new Vector4(g.uv.x, g.uv.y, g.uv.width, g.uv.height));
                    mat.SetFloat("_OrigTangentSpace", g.mats[0].HasProperty("_OrigTangentSpace") ? g.mats[0].GetFloat("_OrigTangentSpace") : 0f);

                    var cov = new byte[W * H];
                    var gray = new Texture2D(W, H, TextureFormat.R8, false, true); temps.Add(gray);
                    foreach (int side in new[] { 1, -1 })
                        for (int deg = 0; deg <= (int)FaceSdfCombine.MaxDeg; deg += StepDeg)
                        {
                            float a = deg * Mathf.Deg2Rad;
                            Vector3 l = fwd * Mathf.Cos(a) + right * (side * Mathf.Sin(a));
                            mat.SetVector("_LightDir", new Vector4(l.x, l.y, l.z, 0f));
                            var px = Render(g, mat, rt, read);
                            var main = new byte[W * H]; var nose = new byte[W * H];
                            for (int i = 0; i < px.Length; i++)
                            {
                                bool on = px[i].a > 127;
                                if (on) cov[i] = 255;
                                main[i] = on ? px[i].r : (byte)0; nose[i] = on ? px[i].g : (byte)255;
                            }
                            WriteGray(gray, main, MaskFile(dir, "main", side, deg));
                            WriteGray(gray, nose, MaskFile(dir, "nose", side, deg));
                        }
                    WriteGray(gray, cov, dir + "/coverage.png");
                    File.WriteAllText(dir + "/info.json", JsonUtility.ToJson(new Info { width = W, height = H, x = g.uv.x, y = g.uv.y, w = g.uv.width, h = g.uv.height, step = StepDeg }, true));
                    rt.Release(); UnityEngine.Object.DestroyImmediate(rt); rt = null;
                    UnityEngine.Object.DestroyImmediate(read); read = null;
                    rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}: {1}x{2}, UV ({3:F3}, {4:F3}) 크기 ({5:F3}, {6:F3}), 앞면의 {7:P0}, 마스크 {8}장 → {9}", g.name, W, H, g.uv.x, g.uv.y, g.uv.width, g.uv.height, share, 4 * ((int)FaceSdfCombine.MaxDeg / StepDeg + 1), dir));
                }
            }
            finally
            {
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (read != null) UnityEngine.Object.DestroyImmediate(read);
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                PrefabUtility.UnloadPrefabContents(root);
            }
            Combine(prefabPath, rep);
            return rep;
        }

        /// <summary>폴더의 마스크를 다시 합쳐 맵을 만든다. 마스크를 고친 뒤 부른다.</summary>
        public static Report Recombine(string prefabPath) { var rep = new Report(); Combine(prefabPath, rep); return rep; }

        static void Combine(string prefabPath, Report rep)
        {
            string maskRoot = MaskRoot(prefabPath);
            if (!Directory.Exists(maskRoot)) { rep.lines.Add("마스크 폴더 없음: " + maskRoot); return; }
            foreach (var dir in Directory.GetDirectories(maskRoot))
            {
                var info = JsonUtility.FromJson<Info>(File.ReadAllText(dir + "/info.json"));
                int W = info.width, H = info.height, N = W * H;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var cov = ReadMask(dir + "/coverage.png", W, H);
                var deg = new List<float>();
                for (int d = 0; d <= (int)FaceSdfCombine.MaxDeg; d += info.step) deg.Add(d);
                var ch = new float[4][];
                int c = 0;
                foreach (var kind in new[] { "main", "nose" })
                    foreach (int side in new[] { 1, -1 })
                    {
                        var masks = new List<bool[]>();
                        foreach (var d in deg) masks.Add(ReadMask(MaskFile(dir.Replace('\\', '/'), kind, side, (int)d), W, H));
                        ch[c++] = FaceSdfCombine.Thresholds(masks, deg, cov, W, H);
                    }
                var px = new Color32[N];
                for (int i = 0; i < N; i++)
                    px[i] = new Color32((byte)Mathf.RoundToInt(ch[0][i] * 255f), (byte)Mathf.RoundToInt(ch[1][i] * 255f), (byte)Mathf.RoundToInt(ch[2][i] * 255f), (byte)Mathf.RoundToInt(ch[3][i] * 255f));
                var tex = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
                tex.SetPixels32(px); tex.Apply();
                string path = MapPath(prefabPath, Path.GetFileName(dir));
                File.WriteAllBytes(path, tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var imp = (TextureImporter)AssetImporter.GetAtPath(path);
                imp.textureType = TextureImporterType.Default; imp.sRGBTexture = false; imp.mipmapEnabled = false; imp.alphaIsTransparency = false;
                imp.alphaSource = TextureImporterAlphaSource.FromInput;
                imp.wrapMode = TextureWrapMode.Clamp; imp.filterMode = FilterMode.Bilinear; imp.npotScale = TextureImporterNPOTScale.None;
                imp.textureCompression = TextureImporterCompression.Uncompressed; imp.maxTextureSize = 8192;
                imp.SaveAndReimport();
                rep.maps.Add(path);
                rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "맵 저장: {0} ({1}x{2}, 합치기 {3:F1}초)", path, W, H, sw.Elapsed.TotalSeconds));
            }
            AssetDatabase.SaveAssets();
            Apply(prefabPath, rep);
        }

        /// <summary>폴더의 맵을 프리팹의 얼굴 재질에 넣는다. 켜고 끄는 상태는 건드리지 않는다.</summary>
        public static void Apply(string prefabPath, Report rep)
        {
            string maskRoot = MaskRoot(prefabPath);
            ForEachFaceMaterial(prefabPath, (m, texName) =>
            {
                string dir = maskRoot + "/" + Safe(texName);
                if (!File.Exists(dir + "/info.json")) return;
                var map = AssetDatabase.LoadAssetAtPath<Texture2D>(MapPath(prefabPath, Safe(texName)));
                if (map == null) return;
                var info = JsonUtility.FromJson<Info>(File.ReadAllText(dir + "/info.json"));
                m.SetTexture("_FaceSDF", map);
                m.SetVector("_FaceSdfRect", new Vector4(info.x, info.y, info.w, info.h));
                EditorUtility.SetDirty(m);
            });
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// 얼굴 명암을 맵에서 읽을지 정한다. 맵이 있는 재질만 바꾸고, 바꾼 재질 수를 돌려준다. 끄면 규칙으로 돌아간다.
        /// 켤 때는 맵을 다시 넣는다. 다시 변환하면 재질이 새로 만들어져 맵 연결이 끊기는데, 그대로 두면 켜도 아무 일도 일어나지 않았다.
        /// </summary>
        public static int SetUse(string prefabPath, bool on)
        {
            if (on) Apply(prefabPath, new Report());
            int n = 0;
            ForEachFaceMaterial(prefabPath, (m, texName) =>
            {
                if (m.GetTexture("_FaceSDF") == null || m.GetTexture("_FaceSDF").name == "white") return;
                m.SetFloat("_UseFaceSdf", on ? 1f : 0f);
                if (on) m.EnableKeyword("_FACE_SDF"); else m.DisableKeyword("_FACE_SDF");
                EditorUtility.SetDirty(m); n++;
            });
            AssetDatabase.SaveAssets();
            return n;
        }

        static void ForEachFaceMaterial(string prefabPath, Action<Material, string> act)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var rig = root.GetComponentInChildren<ToonFaceRig>(true);
                if (rig == null) return;
                var seen = new HashSet<Material>();
                foreach (var r in rig.faceRenderers)
                {
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    foreach (var m in mats)
                    {
                        if (m == null || !seen.Add(m) || !m.HasProperty("_FaceWeightOn") || !m.HasProperty("_FaceSDF")) continue;
                        if (mats.Length > 1 && m.GetFloat("_FaceWeightOn") < 0.5f) continue;
                        var t = m.GetTexture("_Albedo");
                        act(m, t != null ? t.name : "noTexture");
                    }
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        // 얼굴 재질 칸을 텍스처별로 묶는다. 스킨 메시는 지금 자세로 구운 사본을 쓴다 (코·턱 자리가 이 자세의 월드 좌표다).
        static List<Group> CollectGroups(ToonFaceRig rig, List<UnityEngine.Object> temps)
        {
            var groups = new List<Group>(); Vector3 fwd = rig.transform.forward;
            foreach (var r in rig.faceRenderers)
            {
                if (r == null) continue;
                var smr = r as SkinnedMeshRenderer;
                Mesh src = smr != null ? smr.sharedMesh : (r.GetComponent<MeshFilter>() != null ? r.GetComponent<MeshFilter>().sharedMesh : null);
                if (src == null) continue;
                Mesh mesh; Matrix4x4 l2w;
                if (smr != null) { mesh = PosedCopy(smr, src, temps); l2w = Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one); }
                else { mesh = src; l2w = r.localToWorldMatrix; }
                var v = mesh.vertices; var nrm = mesh.normals; var uv = src.uv;
                var fw = new List<Vector2>(); src.GetUVs(2, fw);
                var mats = r.sharedMaterials;
                for (int s = 0; s < src.subMeshCount; s++)
                {
                    var m = mats.Length > 0 ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                    if (m == null || !m.HasProperty("_FaceWeightOn")) continue;
                    bool wOn = m.GetFloat("_FaceWeightOn") > 0.5f;
                    if (mats.Length > 1 && !wOn) continue;
                    var tex = m.GetTexture("_Albedo");
                    string name = Safe(tex != null ? tex.name : "noTexture");
                    var g = groups.Find(x => x.albedo == tex);
                    if (g == null) { g = new Group { albedo = tex, name = name }; groups.Add(g); }
                    g.slots.Add(new Slot { mesh = mesh, l2w = l2w, sub = s, mat = m });
                    if (!g.mats.Contains(m)) g.mats.Add(m);
                    var idx = src.GetTriangles(s);
                    for (int i = 0; i + 2 < idx.Length; i += 3)
                    {
                        int a = idx[i], b = idx[i + 1], c = idx[i + 2];
                        if (wOn && fw.Count == v.Length && fw[a].x <= 0f && fw[b].x <= 0f && fw[c].x <= 0f) continue;
                        Encapsulate(g, uv[a]); Encapsulate(g, uv[b]); Encapsulate(g, uv[c]);
                        if (Vector3.Dot(l2w.MultiplyVector(nrm[a] + nrm[b] + nrm[c]), fwd) > 0f)
                            g.frontArea += 0.5 * Vector3.Cross(l2w.MultiplyPoint3x4(v[b]) - l2w.MultiplyPoint3x4(v[a]), l2w.MultiplyPoint3x4(v[c]) - l2w.MultiplyPoint3x4(v[a])).magnitude;
                    }
                }
            }
            foreach (var g in groups)
            {
                // 샘플링이 UV 조각 가장자리에서 바깥 칸을 읽어도 값이 있게 조금 넓힌다
                float m = 0.02f * Mathf.Max(g.uv.width, g.uv.height);
                g.uv = new Rect(g.uv.x - m, g.uv.y - m, g.uv.width + 2f * m, g.uv.height + 2f * m);
            }
            return groups;
        }

        static void Encapsulate(Group g, Vector2 p)
        {
            if (!g.uvInit) { g.uv = new Rect(p, Vector2.zero); g.uvInit = true; return; }
            float x0 = Mathf.Min(g.uv.xMin, p.x), y0 = Mathf.Min(g.uv.yMin, p.y), x1 = Mathf.Max(g.uv.xMax, p.x), y1 = Mathf.Max(g.uv.yMax, p.y);
            g.uv = Rect.MinMaxRect(x0, y0, x1, y1);
        }

        // 지금 자세의 위치·노멀·탄젠트에 원본 메시의 UV·삼각형을 붙인 사본. 원본 노멀(UV3·UV4)을 그대로 읽게 한다.
        static Mesh PosedCopy(SkinnedMeshRenderer smr, Mesh src, List<UnityEngine.Object> temps)
        {
            var baked = new Mesh(); temps.Add(baked); smr.BakeMesh(baked, false);
            var m = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 }; temps.Add(m);
            m.vertices = baked.vertices; m.normals = baked.normals; m.tangents = baked.tangents; m.uv = src.uv;
            foreach (int ch in new[] { 2, 3, 4 })
            {
                var l = new List<Vector3>(); src.GetUVs(ch, l);
                if (l.Count == src.vertexCount) m.SetUVs(ch, l);
            }
            m.subMeshCount = src.subMeshCount;
            for (int s = 0; s < src.subMeshCount; s++) m.SetTriangles(src.GetTriangles(s), s, false);
            m.RecalculateBounds();
            return m;
        }

        // 커맨드 버퍼로 그린다. GL 즉시 그리기는 에디터 창이 가려지면 일부만 그렸다 (FBX2NPRParseBake 와 같은 이유).
        static Color32[] Render(Group g, Material mat, RenderTexture rt, Texture2D read)
        {
            var cb = new CommandBuffer { name = "FBX2NPR face mask" };
            cb.SetRenderTarget(rt);
            cb.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 0f));
            foreach (var s in g.slots) cb.DrawMesh(s.mesh, s.l2w, mat, s.sub, 0);
            Graphics.ExecuteCommandBuffer(cb); cb.Release();
            var prev = RenderTexture.active; RenderTexture.active = rt;
            read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); read.Apply(false);
            RenderTexture.active = prev;
            return read.GetPixels32();
        }

        static void WriteGray(Texture2D gray, byte[] v, string path)
        {
            gray.SetPixelData(v, 0); gray.Apply(false);
            File.WriteAllBytes(path, gray.EncodeToPNG());
        }

        static bool[] ReadMask(string path, int W, int H)
        {
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            try
            {
                if (!t.LoadImage(File.ReadAllBytes(path)) || t.width != W || t.height != H) throw new InvalidDataException("마스크 크기가 맞지 않습니다: " + path);
                var px = t.GetPixels32();
                var o = new bool[W * H];
                for (int i = 0; i < o.Length; i++) o[i] = px[i].r >= 128;
                return o;
            }
            finally { UnityEngine.Object.DestroyImmediate(t); }
        }
    }
}
