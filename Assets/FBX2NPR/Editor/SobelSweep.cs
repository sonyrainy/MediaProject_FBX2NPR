using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 선 굵기와 민감도를 훑습니다.
    /// 1차 검증에서 화면공간 두께 일정 조건은 통과했지만 평균 폭이 11.8px 로 너무 굵었고
    /// 실루엣 둘레에 넓은 후광이 생겼습니다. 원인 후보는 깊이 항입니다.
    /// 깊이 민감도를 0 으로 두고 노멀만 쓰는 경우까지 같이 봅니다.
    /// </summary>
    public static class SobelSweep
    {
        const string Head = "Assets/NormalTest/Head_mix000.fbx";
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const int Res = 700;
        const float FovY = 24f;

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Console.WriteLine("[sweep] " + m); }

        [MenuItem("FBX2NPR/외곽선 파라미터 훑기")]
        public static void Run()
        {
            Log.Clear();
            try
            {
                SobelOutlineFeature feat = null;
                var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
                foreach (var f in rd.rendererFeatures) { feat = f as SobelOutlineFeature; if (feat != null) break; }
                if (feat == null) { L("피처 없음. 먼저 검증을 실행하세요."); Finish(); return; }
                feat.SetActive(true);
                feat.settings.debug = 0f;

                var mi = AssetImporter.GetAtPath(Head) as ModelImporter;
                mi.importNormals = ModelImporterNormals.Import;
                mi.weldVertices = false; mi.meshOptimizationFlags = 0;
                mi.meshCompression = ModelImporterMeshCompression.Off; mi.isReadable = true;
                mi.SaveAndReimport();

                Mesh src = null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Head)) { var m = o as Mesh; if (m != null) { src = m; break; } }
                var mesh = UnityEngine.Object.Instantiate(src);
                var v = mesh.vertices; var orig = mesh.normals;
                mesh.SetUVs(3, new List<Vector3>(orig));
                mesh.normals = RadialNormals.Apply(v, orig, RadialNormals.Centroid(v), 1f);

                var mat = new Material(Shader.Find("NormalTest/ToonBand"));
                mat.SetVector("_Band0", new Vector4(0.32f, 0.30f, 0.42f, 1f));
                mat.SetVector("_Band1", new Vector4(0.72f, 0.66f, 0.70f, 1f));
                mat.SetVector("_Band2", new Vector4(1.00f, 0.96f, 0.92f, 1f));
                mat.SetFloat("_Stop1", 0.267f); mat.SetFloat("_Stop2", 0.487f);
                mat.SetFloat("_UseOrigNormal", 1f);
                float e = 25f * Mathf.Deg2Rad, a = 35f * Mathf.Deg2Rad;
                mat.SetVector("_LightDir", (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a))
                    + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a)) + Vector3.up * Mathf.Sin(e)).normalized);

                ShaderUtil.allowAsyncCompilation = false;

                // 앞선 훑기에서 노멀 그래디언트가 포화돼 있는 것이 확인됐습니다.
                // 문턱값이 아니라 노멀 민감도를 낮춰야 선이 가늘어집니다.
                // 실루엣은 깊이 항이 얇게(평균 2.5px) 잘 잡아 주므로 같이 켜 둡니다.
                var ns = new float[] { 1.2f, 0.8f, 0.5f, 0.5f, 0.3f, 0.3f };
                var th = new float[] { 0.30f, 0.30f, 0.30f, 0.50f, 0.30f, 0.50f };
                var ds = new float[] { 12f, 12f, 12f, 12f, 12f, 12f };
                var tk = new float[] { 1.0f, 1.0f, 1.0f, 1.0f, 1.0f, 1.0f };

                L("설정별 결과 (선폭은 가로 주사선에서 잰 어두운 구간)");
                for (int i = 0; i < ds.Length; i++)
                {
                    feat.settings.depthScale = ds[i];
                    feat.settings.normalScale = ns[i];
                    feat.settings.threshold = th[i];
                    feat.settings.thickness = tk[i];
                    Dirty();
                    string f = string.Format(CultureInfo.InvariantCulture, "swp_{0}.png", i);
                    var r = Shoot(mesh, mat, 1.20f, f);
                    L(string.Format(CultureInfo.InvariantCulture,
                        "  [{0}] 깊이{1,4:F1} 노멀{2,4:F1} 문턱{3:F2}  선화소 {4,6} ({5,5:F2}%)  평균폭 {6,5:F2}px  중앙값 {7}px  최대 {8}px",
                        i, ds[i], ns[i], th[i], r.dark, 100.0 * r.dark / Mathf.Max(r.lit, 1), r.avg, r.med, r.max));
                }
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            catch (Exception ex) { L("예외: " + ex); }
            Finish();
        }

        static void Dirty()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            var m = rd.GetType().GetMethod("SetDirty", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (m != null) m.Invoke(rd, null);
        }

        struct Res2 { public int lit, dark, med, max; public float avg; }

        static Res2 Shoot(Mesh mesh, Material mat, float distK, string file)
        {
            var root = new GameObject("Rig"); root.hideFlags = HideFlags.HideAndDontSave;
            var go = new GameObject("Head"); go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;

            Bounds b = mesh.bounds;
            float r = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 0.5f;
            float dist = r / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * distK;

            var camGo = new GameObject("Cam"); camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.fieldOfView = FovY; cam.aspect = 1f;
            cam.nearClipPlane = Mathf.Max(0.001f, dist - r * 3f); cam.farClipPlane = dist + r * 8f;
            cam.allowHDR = false; cam.allowMSAA = false;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None;
            acd.dithering = false; acd.renderShadows = false;
            camGo.transform.position = b.center + Vector3.right * dist;
            camGo.transform.rotation = Quaternion.LookRotation(b.center - camGo.transform.position, Vector3.up);

            var rt = new RenderTexture(Res, Res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            bool done = false;
            try
            {
                var rq = new UniversalRenderPipeline.SingleCameraRequest(); rq.destination = rt;
                if (RenderPipeline.SupportsRenderRequest(cam, rq)) { cam.SubmitRenderRequest(rq); done = true; }
            }
            catch { }
            if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(Res, Res, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, Res, Res), 0, 0); t.Apply();
            RenderTexture.active = prev;

            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, file), t.EncodeToPNG());

            var px = t.GetPixels32();
            var res = new Res2();
            var runs = new List<int>();
            for (int y = 0; y < Res; y++)
            {
                int run = 0;
                for (int x = 0; x < Res; x++)
                {
                    var p = px[y * Res + x];
                    int s = (p.r + p.g + p.b) / 3;
                    if (s > 8) res.lit++;
                    bool dk = s > 8 && s < 130;
                    if (dk) { res.dark++; run++; }
                    else { if (run > 0) runs.Add(run); run = 0; }
                }
                if (run > 0) runs.Add(run);
            }
            if (runs.Count > 0)
            {
                runs.Sort();
                long sum = 0; foreach (var q in runs) sum += q;
                res.avg = (float)sum / runs.Count;
                res.med = runs[runs.Count / 2];
                res.max = runs[runs.Count - 1];
            }
            UnityEngine.Object.DestroyImmediate(t); rt.Release();
            UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(root);
            return res;
        }

        static void Finish()
        {
            string d = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "log_sweep.txt"), Log.ToString(), new UTF8Encoding(true));
        }
    }
}
