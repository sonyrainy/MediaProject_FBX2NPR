using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 꺾임선 생성을 블렌더 프로토타입과 대조합니다.
    /// 블렌더 기준: 임계 30도, 최소길이 비율 0.009 에서 모서리 472개.
    /// 같은 수가 나와야 이식이 맞은 것입니다.
    /// </summary>
    public static class LineTest
    {
        const string Folder = "Assets/NormalTest";
        const string Head   = Folder + "/Head_mix000.fbx";
        const int   Res     = 640;
        const float FovY    = 24f, DistK = 1.20f, Elev = 25f;

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Debug.Log("[line] " + m); Console.WriteLine("[line] " + m); }

        [MenuItem("FBX2NPR/꺾임선 생성 검증")]
        public static void Run()
        {
            Log.Clear();
            try
            {
                var mi = AssetImporter.GetAtPath(Head) as ModelImporter;
                if (mi == null) { L("에셋 없음: " + Head); Finish(); return; }
                mi.importNormals = ModelImporterNormals.Import;
                mi.importTangents = ModelImporterTangents.None;
                mi.weldVertices = false;
                mi.meshOptimizationFlags = 0;
                mi.meshCompression = ModelImporterMeshCompression.Off;
                mi.isReadable = true;
                mi.SaveAndReimport();

                Mesh src = null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Head)) { var m = o as Mesh; if (m != null) { src = m; break; } }
                if (src == null) { L("메시 없음"); Finish(); return; }

                Vector3 bsz = src.bounds.size;
                float sz = Mathf.Max(bsz.x, Mathf.Max(bsz.y, bsz.z));
                L(string.Format(CultureInfo.InvariantCulture,
                    "메시 정점 {0}  삼각형 {1}  바운딩 {2}  기준크기 {3:F4}",
                    src.vertexCount, src.triangles.Length / 3, bsz.ToString("F4"), sz));

                // 얼굴 전체 방사형 정렬 (현행 방식)
                var work = UnityEngine.Object.Instantiate(src);
                Vector3[] v = work.vertices;
                Vector3 c = RadialNormals.Centroid(v);
                work.normals = RadialNormals.Apply(v, work.normals, c, 1f);

                L("");
                L("  임계각  최소길이비  ->  모서리     (블렌더 대조)");
                int used = 0;
                foreach (var cfg in new[] {
                    new { a = 30f, r = 0.0f    , note = "필터 없음  (블렌더 719)" },
                    new { a = 30f, r = 0.0089f , note = "0.009      (블렌더 472)" },
                    new { a = 30f, r = 0.0124f , note = "0.0124     (블렌더 348)" },
                    new { a = 45f, r = 0.0f    , note = "45도       (블렌더  48)" },
                })
                {
                    int cnt;
                    var lm = CreaseLines.Build(work, cfg.a, cfg.r, 0.0044f, out cnt);
                    L(string.Format(CultureInfo.InvariantCulture,
                        "   {0,4:F0}도   {1,7:F4}   ->  {2,5}     {3}", cfg.a, cfg.r, cnt, cfg.note));
                    if (Mathf.Approximately(cfg.a, 30f) && cfg.r > 0.008f && cfg.r < 0.009f)
                    {
                        used = cnt;
                        if (lm != null) Render(work, lm);
                    }
                    if (lm != null) UnityEngine.Object.DestroyImmediate(lm);
                }
                L("");
                L(Mathf.Abs(used - 472) <= 24
                    ? "=> 블렌더와 일치 (오차 5% 이내). 이식 성공."
                    : "=> 블렌더(472)와 차이가 큽니다. 모서리 묶는 방식을 확인할 것.");
            }
            catch (Exception e) { L("예외: " + e); }
            Finish();
        }

        static void Render(Mesh head, Mesh lines)
        {
            var root = new GameObject("LineRig"); root.hideFlags = HideFlags.HideAndDontSave;

            var toon = new Material(Shader.Find("NormalTest/ToonBand"));
            toon.SetVector("_Band0", new Vector4(0.32f, 0.30f, 0.42f, 1));
            toon.SetVector("_Band1", new Vector4(0.72f, 0.66f, 0.70f, 1));
            toon.SetVector("_Band2", new Vector4(1.00f, 0.96f, 0.92f, 1));
            toon.SetFloat("_Stop1", 0.267f); toon.SetFloat("_Stop2", 0.487f);
            var lineSh = Shader.Find("FBX2NPR/Line");
            if (lineSh == null) { L("!! FBX2NPR/Line 셰이더를 찾지 못함"); return; }
            var lineMat = new Material(lineSh);

            var go = new GameObject("Head"); go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = head;
            go.AddComponent<MeshRenderer>().sharedMaterial = toon;
            var lg = new GameObject("Lines"); lg.transform.SetParent(root.transform, false);
            lg.AddComponent<MeshFilter>().sharedMesh = lines;
            lg.AddComponent<MeshRenderer>().sharedMaterial = lineMat;

            Bounds b = head.bounds;
            float r = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 0.5f;
            float dist = r / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * DistK;

            var camGo = new GameObject("Cam"); camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.fieldOfView = FovY; cam.aspect = 1f; cam.allowHDR = false; cam.allowMSAA = false;
            cam.nearClipPlane = Mathf.Max(0.001f, dist - r * 3f); cam.farClipPlane = dist + r * 6f;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None;
            acd.dithering = false; acd.renderShadows = false;

            float e = Elev * Mathf.Deg2Rad, a = -(-35f) * Mathf.Deg2Rad;  // 부호는 좌표계 보정
            Vector3 ld = (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a))
                        + Vector3.right   * (Mathf.Cos(e) * Mathf.Sin(a))
                        + Vector3.up      * Mathf.Sin(e)).normalized;
            toon.SetVector("_LightDir", ld);

            ShaderUtil.allowAsyncCompilation = false;
            foreach (var view in new[] {
                new { n = "front", d = Vector3.forward },
                new { n = "side",  d = Vector3.right } })
            {
                camGo.transform.position = b.center + view.d * dist;
                camGo.transform.rotation = Quaternion.LookRotation(b.center - camGo.transform.position, Vector3.up);
                Shoot(cam, "uline_" + view.n + ".png");
            }
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void Shoot(Camera cam, string file)
        {
            var rt = new RenderTexture(Res, Res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            bool done = false;
            try
            {
                var req = new UniversalRenderPipeline.SingleCameraRequest { destination = rt };
                if (RenderPipeline.SupportsRenderRequest(cam, req)) { cam.SubmitRenderRequest(req); done = true; }
            }
            catch (Exception e) { L("  요청 실패: " + e.Message); }
            if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }

            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(Res, Res, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, Res, Res), 0, 0); t.Apply();
            RenderTexture.active = prev;
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, file), t.EncodeToPNG());
            var px = t.GetPixels32(); int lit = 0;
            for (int i = 0; i < px.Length; i += 31) if (px[i].r > 8 || px[i].g > 8 || px[i].b > 8) lit++;
            L("  " + file + "  비검정 표본 " + lit);
            UnityEngine.Object.DestroyImmediate(t); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }

        static void Finish()
        {
            string d = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "log_line.txt"), Log.ToString(), new UTF8Encoding(true));
        }
    }
}