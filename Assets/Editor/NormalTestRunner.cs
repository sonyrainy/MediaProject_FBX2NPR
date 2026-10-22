using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NormalTest
{
    /// <summary>
    /// 블렌더에서 만든 두 FBX(원본 노멀 / 구형 노멀)를 유니티에서 같은 조건으로 재는 도구입니다.
    ///
    /// 렌더보다 데이터를 먼저 봅니다. Mesh.normals 를 직접 꺼내 각도 차이를 재서
    /// 유니티가 파일의 커스텀 노멀을 실제로 썼는지부터 확인합니다.
    /// 이걸 안 하면 그림이 같아 보일 때 셰이더 탓인지 임포터 탓인지 구분할 수 없습니다.
    /// </summary>
    public static class Runner
    {
        const string Folder = "Assets/NormalTest";
        const string PathA  = Folder + "/Head_Original.fbx";
        const string PathB  = Folder + "/Head_Spherized.fbx";
        const string ShaderName = "NormalTest/ToonBand";

        // 블렌더 렌더와 공유하는 규약입니다. 한 쪽만 바꾸면 비교가 깨집니다.
        const int   Res    = 640;
        const float FovY   = 24f;
        const float DistK  = 1.20f;
        const float Elev   = 25f;
        static readonly (string name, float az)[] Azimuths =
            { ("L0", -35f), ("L1", 0f), ("L2", 40f) };
        static readonly Vector4 Band0 = new Vector4(0.32f, 0.30f, 0.42f, 1f);
        static readonly Vector4 Band1 = new Vector4(0.72f, 0.66f, 0.70f, 1f);
        static readonly Vector4 Band2 = new Vector4(1.00f, 0.96f, 0.92f, 1f);
        const float Stop1 = 0.267f;
        const float Stop2 = 0.487f;

        static string OutDir
        {
            get
            {
                string d = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m)
        {
            Log.AppendLine(m);
            Debug.Log("[nt] " + m);
            Console.WriteLine("[nt] " + m);
        }

        // ---------------------------------------------------------------- 1단계

        [MenuItem("NormalTest/1. 임포트 + 노멀 측정 + 방향 확인")]
        public static void Probe()
        {
            Log.Clear();
            try
            {
                ApplyImportSettings(PathA);
                ApplyImportSettings(PathB);

                Mesh ma = LoadMesh(PathA);
                Mesh mb = LoadMesh(PathB);
                if (ma == null || mb == null) { Finish("probe"); return; }

                CompareNormals(ma, mb);
                RenderFourViews(ma);
            }
            catch (Exception e)
            {
                L("예외: " + e);
            }
            Finish("probe");
        }

        /// <summary>
        /// 임포터가 조용히 개입하지 못하게 막습니다.
        /// Weld Vertices 와 Optimize Mesh 를 끄는 이유가 중요합니다.
        /// 구형 정렬을 하면 원래 갈라져 있던 정점들의 노멀이 같아지는데,
        /// 웰딩이 켜져 있으면 그 정점들이 병합되어 두 메시의 정점 수가 달라집니다.
        /// 그러면 인덱스 대 인덱스 비교가 성립하지 않습니다.
        /// </summary>
        static void ApplyImportSettings(string path)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) { L("임포터를 찾지 못함: " + path); return; }

            mi.importNormals        = ModelImporterNormals.Import;
            mi.importTangents       = ModelImporterTangents.None;
            mi.importBlendShapes    = false;
            mi.weldVertices         = false;
            mi.meshOptimizationFlags = 0;
            mi.meshCompression      = ModelImporterMeshCompression.Off;
            mi.importAnimation      = false;
            mi.animationType        = ModelImporterAnimationType.None;
            mi.importCameras        = false;
            mi.importLights         = false;
            mi.isReadable           = true;

            mi.SaveAndReimport();
            L(string.Format("임포트 설정 적용: {0}  normals={1} weld={2} optimize={3} compression={4}",
                Path.GetFileName(path), mi.importNormals, mi.weldVertices,
                (int)mi.meshOptimizationFlags, mi.meshCompression));
        }

        static Mesh LoadMesh(string path)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                var m = o as Mesh;
                if (m != null) return m;
            }
            L("메시를 찾지 못함: " + path);
            return null;
        }

        static void CompareNormals(Mesh a, Mesh b)
        {
            Vector3[] na = a.normals, nb = b.normals;
            L(string.Format("정점 {0} / {1}    노멀 {2} / {3}",
                a.vertexCount, b.vertexCount, na.Length, nb.Length));

            if (na.Length == 0 || nb.Length == 0)
            {
                L("!! 노멀 배열이 비어 있습니다. 임포트 설정을 의심할 것.");
                return;
            }
            if (na.Length != nb.Length)
            {
                L("!! 노멀 개수가 다릅니다. 인덱스 비교 불가 — Weld/Optimize 가 켜졌는지 확인할 것.");
                return;
            }

            var angs = new List<float>(na.Length);
            int moved = 0;
            for (int i = 0; i < na.Length; i++)
            {
                float d = Mathf.Clamp(Vector3.Dot(na[i].normalized, nb[i].normalized), -1f, 1f);
                float t = Mathf.Acos(d) * Mathf.Rad2Deg;
                angs.Add(t);
                if (t > 1f) moved++;
            }
            angs.Sort();
            int n = angs.Count;
            double sum = 0; for (int i = 0; i < n; i++) sum += angs[i];

            L(string.Format(CultureInfo.InvariantCulture,
                "노멀 각도차  평균 {0:F2}도  중앙 {1:F2}도  p95 {2:F2}도  최대 {3:F2}도  |  1도 초과 {4}/{5} ({6:F0}%)",
                sum / n, angs[n / 2], angs[(int)(n * 0.95)], angs[n - 1], moved, n, 100.0 * moved / n));
            L("  블렌더 기준값: 평균 52.34도 / 중앙 47.79도 / 최대 160.96도 / 100%");
            L(Mathf.Abs((float)(sum / n) - 52.34f) < 2f
                ? "  => 일치. 유니티가 파일의 커스텀 노멀을 그대로 썼습니다."
                : "  => 불일치. 임포터가 개입했습니다. 렌더 비교로 넘어가지 말 것.");
        }

        // ---------------------------------------------------------------- 렌더

        static Material MakeMaterial()
        {
            var sh = Shader.Find(ShaderName);
            if (sh == null) { L("셰이더를 찾지 못함: " + ShaderName); return null; }
            var m = new Material(sh);
            m.SetVector("_Band0", Band0);
            m.SetVector("_Band1", Band1);
            m.SetVector("_Band2", Band2);
            m.SetFloat("_Stop1", Stop1);
            m.SetFloat("_Stop2", Stop2);
            return m;
        }

        struct Rig
        {
            public GameObject root, go, camGo, lightGo;
            public Camera cam;
            public Material mat;
            public Vector3 center;
            public float radius, dist;
        }

        static Rig BuildRig(Mesh mesh)
        {
            var r = new Rig();
            r.root = new GameObject("NormalTestRig");
            r.root.hideFlags = HideFlags.HideAndDontSave;

            r.mat = MakeMaterial();
            r.go = new GameObject("Head");
            r.go.transform.SetParent(r.root.transform, false);
            r.go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = r.go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = r.mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            Bounds b = mesh.bounds;
            r.center = r.go.transform.TransformPoint(b.center);
            Vector3 sz = Vector3.Scale(b.size, r.go.transform.lossyScale);
            r.radius = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z)) * 0.5f;
            r.dist = r.radius / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * DistK;

            r.camGo = new GameObject("Cam");
            r.camGo.transform.SetParent(r.root.transform, false);
            r.cam = r.camGo.AddComponent<Camera>();
            r.cam.clearFlags = CameraClearFlags.SolidColor;
            r.cam.backgroundColor = Color.black;
            r.cam.orthographic = false;
            r.cam.fieldOfView = FovY;
            r.cam.aspect = 1f;
            r.cam.nearClipPlane = Mathf.Max(0.01f, r.dist - r.radius * 3f);
            r.cam.farClipPlane = r.dist + r.radius * 6f;
            r.cam.allowHDR = false;
            r.cam.allowMSAA = false;

            var acd = r.camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false;
            acd.antialiasing = AntialiasingMode.None;
            acd.dithering = false;
            acd.renderShadows = false;
            acd.requiresColorOption = CameraOverrideOption.Off;
            acd.requiresDepthOption = CameraOverrideOption.Off;

            L(string.Format(CultureInfo.InvariantCulture,
                "리그: 중심={0}  반지름={1:F3}  거리={2:F3}  FOV={3}",
                r.center.ToString("F3"), r.radius, r.dist, FovY));
            return r;
        }

        static void Shoot(Rig r, string file)
        {
            var rt = new RenderTexture(Res, Res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();

            string how = "Camera.Render";
            bool done = false;
            try
            {
                var req = new UniversalRenderPipeline.SingleCameraRequest();
                req.destination = rt;
                if (RenderPipeline.SupportsRenderRequest(r.cam, req))
                {
                    r.cam.SubmitRenderRequest(req);
                    how = "SingleCameraRequest";
                    done = true;
                }
            }
            catch (Exception e)
            {
                L("  SingleCameraRequest 실패 -> " + e.GetType().Name + ": " + e.Message);
            }
            if (!done)
            {
                r.cam.targetTexture = rt;
                r.cam.Render();
                r.cam.targetTexture = null;
            }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(Res, Res, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, Res, Res), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            string p = Path.Combine(OutDir, file);
            File.WriteAllBytes(p, tex.EncodeToPNG());

            // 전부 검정이면 렌더가 실패한 것이므로 조용히 넘어가지 않게 검사합니다.
            var px = tex.GetPixels32();
            int lit = 0;
            for (int i = 0; i < px.Length; i += 7) if (px[i].r > 8 || px[i].g > 8 || px[i].b > 8) lit++;
            L(string.Format("  {0}  [{1}]  비검정 표본 {2}", file, how, lit));

            UnityEngine.Object.DestroyImmediate(tex);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
        }

        /// <summary>FBX 왕복 뒤 유니티 좌표계에서 얼굴이 어느 쪽을 보는지 눈으로 확인합니다.</summary>
        static void RenderFourViews(Mesh mesh)
        {
            var r = BuildRig(mesh);
            if (r.mat == null) return;

            ShaderUtil.allowAsyncCompilation = false;
            Vector3[] dirs = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
            string[] names = { "pZ", "pX", "nZ", "nX" };

            for (int i = 0; i < dirs.Length; i++)
            {
                r.camGo.transform.position = r.center + dirs[i] * r.dist;
                r.camGo.transform.rotation = Quaternion.LookRotation(r.center - r.camGo.transform.position, Vector3.up);
                // 방향 확인용이라 카메라 쪽에서 비춥니다.
                r.mat.SetVector("_LightDir", -r.camGo.transform.forward);
                if (i == 0) Shoot(r, "warmup.png");   // 첫 렌더는 셰이더 컴파일 때문에 버립니다
                Shoot(r, "view_" + names[i] + ".png");
            }
            UnityEngine.Object.DestroyImmediate(r.root);
        }

        // ---------------------------------------------------------------- 2단계

        [MenuItem("NormalTest/2. 본 비교 렌더")]
        public static void Render()
        {
            Log.Clear();
            try
            {
                Mesh ma = LoadMesh(PathA);
                Mesh mb = LoadMesh(PathB);
                if (ma == null || mb == null) { Finish("render"); return; }

                ShaderUtil.allowAsyncCompilation = false;
                bool warmed = false;

                foreach (var pair in new[] { ("orig", ma), ("sphr", mb) })
                {
                    var r = BuildRig(pair.Item2);
                    if (r.mat == null) return;

                    r.camGo.transform.position = r.center + Vector3.forward * r.dist;
                    r.camGo.transform.rotation =
                        Quaternion.LookRotation(r.center - r.camGo.transform.position, Vector3.up);

                    foreach (var a in Azimuths)
                    {
                        Vector3 ld = LightDir(a.az);
                        r.mat.SetVector("_LightDir", ld);
                        if (!warmed) { Shoot(r, "warmup2.png"); warmed = true; }
                        Shoot(r, string.Format("unity_{0}_{1}.png", pair.Item1, a.name));
                        if (pair.Item1 == "orig")
                        {
                            L(string.Format(CultureInfo.InvariantCulture,
                                "    {0} az={1:F0}  L=({2:F4}, {3:F4}, {4:F4})", a.name, a.az, ld.x, ld.y, ld.z));
                        }
                    }

                    // 노멀 자체를 색으로도 한 장 남깁니다. 그림이 같아 보일 때 데이터를 눈으로 대조하려고요.
                    r.mat.SetFloat("_ShowNormal", 1f);
                    Shoot(r, string.Format("unity_{0}_normal.png", pair.Item1));
                    r.mat.SetFloat("_ShowNormal", 0f);

                    UnityEngine.Object.DestroyImmediate(r.root);
                }
            }
            catch (Exception e)
            {
                L("예외: " + e);
            }
            Finish("render");
        }

        /// <summary>블렌더 스크립트와 글자 그대로 같은 식입니다. 빛이 오는 쪽 단위벡터.</summary>
        static Vector3 LightDir(float azDeg)
        {
            float e = Elev * Mathf.Deg2Rad;
            // 방위각 부호를 뒤집습니다. 블렌더는 오른손, 유니티는 왼손 좌표계라
            // FBX 임포터가 X축을 뒤집어 받습니다. 실측 확인: 부호를 맞추기 전에는
            // 블렌더 L0 와 유니티 L2 가 7.98%로 붙고 같은 이름끼리는 60% 넘게 벌어졌습니다.
            float a = -azDeg * Mathf.Deg2Rad;
            Vector3 front = Vector3.forward, right = Vector3.right, up = Vector3.up;
            return (front * (Mathf.Cos(e) * Mathf.Cos(a))
                  + right * (Mathf.Cos(e) * Mathf.Sin(a))
                  + up    * Mathf.Sin(e)).normalized;
        }
        // ---------------------------------------------------------------- 3단계

        const string TexPath = Folder + "/Head_Normal.png";

        [MenuItem("NormalTest/3. 노멀 맵 충돌 실험 (12장)")]
        public static void RenderNormalMap()
        {
            Log.Clear();
            try
            {
                // 노멀 맵을 raw 로 읽습니다. NormalMap 타입은 DXT5nm/BC5 로 압축하면서
                // 채널을 스위즐하므로, 언팩 변종을 틀리면 조용히 엉뚱한 값이 나옵니다.
                // Default + sRGB 끔 + 무압축이면 셰이더에서 rgb*2-1 만 하면 됩니다.
                var ti = AssetImporter.GetAtPath(TexPath) as TextureImporter;
                if (ti == null) { L("텍스처를 찾지 못함: " + TexPath); Finish("nmap"); return; }
                ti.textureType         = TextureImporterType.Default;
                ti.sRGBTexture         = false;
                ti.mipmapEnabled       = false;
                ti.filterMode          = FilterMode.Bilinear;
                ti.wrapMode            = TextureWrapMode.Clamp;
                ti.maxTextureSize      = 4096;
                ti.textureCompression  = TextureImporterCompression.Uncompressed;
                ti.npotScale           = TextureImporterNPOTScale.None;
                ti.isReadable          = true;
                ti.SaveAndReimport();

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
                if (tex == null) { L("텍스처 로드 실패"); Finish("nmap"); return; }
                L(string.Format("텍스처 {0}x{1}  format={2}  mips={3}",
                    tex.width, tex.height, tex.format, tex.mipmapCount));

                // 탄젠트가 있어야 탄젠트 공간 맵을 쓸 수 있습니다.
                foreach (var p in new[] { PathA, PathB })
                {
                    var mi = AssetImporter.GetAtPath(p) as ModelImporter;
                    mi.importTangents = ModelImporterTangents.CalculateMikk;
                    mi.SaveAndReimport();
                }

                Mesh ma = LoadMesh(PathA), mb = LoadMesh(PathB);
                if (ma == null || mb == null) { Finish("nmap"); return; }

                L(string.Format("UV {0} / {1}    탄젠트 {2} / {3}",
                    ma.uv.Length, mb.uv.Length, ma.tangents.Length, mb.tangents.Length));
                if (ma.uv.Length == 0 || ma.tangents.Length == 0)
                {
                    L("!! UV 또는 탄젠트가 없습니다. 노멀 맵이 적용될 수 없습니다.");
                    Finish("nmap"); return;
                }

                // 탄젠트를 켠 것이 정점 노멀을 건드렸는지 반드시 재확인합니다.
                // 값이 달라졌다면 앞서 낸 52.3도 측정이 무효가 됩니다.
                L("-- 탄젠트 활성화 후 노멀 재검증 --");
                CompareNormals(ma, mb);

                ShaderUtil.allowAsyncCompilation = false;
                bool warmed = false;

                foreach (var pair in new[] { ("orig", ma), ("sphr", mb) })
                {
                    var r = BuildRig(pair.Item2);
                    if (r.mat == null) return;
                    r.mat.SetTexture("_NormalMap", tex);

                    r.camGo.transform.position = r.center + Vector3.forward * r.dist;
                    r.camGo.transform.rotation =
                        Quaternion.LookRotation(r.center - r.camGo.transform.position, Vector3.up);

                    foreach (float strength in new[] { 0f, 1f })
                    {
                        r.mat.SetFloat("_NormalStrength", strength);
                        string tag = strength > 0.5f ? "mapOn" : "mapOff";

                        foreach (var a in Azimuths)
                        {
                            r.mat.SetVector("_LightDir", LightDir(a.az));
                            if (!warmed) { Shoot(r, "warmup3.png"); warmed = true; }
                            Shoot(r, string.Format("nm_{0}_{1}_{2}.png", pair.Item1, tag, a.name));
                        }

                        // 맵이 실제로 노멀을 흔들고 있는지 눈으로 대조할 수 있게 남깁니다.
                        r.mat.SetFloat("_ShowNormal", 1f);
                        Shoot(r, string.Format("nm_{0}_{1}_normal.png", pair.Item1, tag));
                        r.mat.SetFloat("_ShowNormal", 0f);
                    }
                    UnityEngine.Object.DestroyImmediate(r.root);
                }
            }
            catch (Exception e)
            {
                L("예외: " + e);
            }
            Finish("nmap");
        }
        [MenuItem("NormalTest/0. 전부 실행")]
        public static void RunAll()
        {
            // 배치모드 한 번으로 세 단계를 이어서 돌립니다. 각 단계가 자기 로그를 따로 남깁니다.
            Probe();            // 임포트 강제 + 노멀 각도 측정 (탄젠트 끔, 인덱스 비교 가능)
            Render();           // 블렌더 대조용 6장
            RenderNormalMap();  // 노멀 맵 충돌 12장 (탄젠트 켬)
        }
        // ---------------------------------------------------------------- 4단계 : 격자

        static readonly int[] MixLevels      = { 0, 20, 40, 60, 80, 100 };
        static readonly int[] StrengthLevels = { 0, 25, 50, 75, 100 };

        [MenuItem("NormalTest/4. 격자 실험 (90장)")]
        public static void RenderGrid()
        {
            Log.Clear();
            try
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
                if (tex == null) { L("텍스처 없음: " + TexPath); Finish("grid"); return; }
                L(string.Format("텍스처 {0}x{1} format={2} mips={3}",
                    tex.width, tex.height, tex.format, tex.mipmapCount));

                ShaderUtil.allowAsyncCompilation = false;
                bool warmed = false;
                int shots = 0;

                foreach (int mix in MixLevels)
                {
                    string path = string.Format("{0}/Head_mix{1:000}.fbx", Folder, mix);

                    var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (mi == null) { L("임포터 없음: " + path); continue; }
                    mi.importNormals         = ModelImporterNormals.Import;
                    mi.importTangents        = ModelImporterTangents.CalculateMikk;
                    mi.importBlendShapes     = false;
                    mi.weldVertices          = false;
                    mi.meshOptimizationFlags = 0;
                    mi.meshCompression       = ModelImporterMeshCompression.Off;
                    mi.importAnimation       = false;
                    mi.animationType         = ModelImporterAnimationType.None;
                    mi.isReadable            = true;
                    mi.SaveAndReimport();

                    Mesh mesh = LoadMesh(path);
                    if (mesh == null) continue;

                    var r = BuildRig(mesh);
                    if (r.mat == null) { Finish("grid"); return; }
                    r.mat.SetTexture("_NormalMap", tex);
                    r.camGo.transform.position = r.center + Vector3.forward * r.dist;
                    r.camGo.transform.rotation =
                        Quaternion.LookRotation(r.center - r.camGo.transform.position, Vector3.up);

                    L(string.Format("mix={0,3}  정점 {1}  UV {2}  탄젠트 {3}",
                        mix, mesh.vertexCount, mesh.uv.Length, mesh.tangents.Length));

                    foreach (int st in StrengthLevels)
                    {
                        r.mat.SetFloat("_NormalStrength", st / 100f);
                        foreach (var a in Azimuths)
                        {
                            r.mat.SetVector("_LightDir", LightDir(a.az));
                            if (!warmed) { Shoot(r, "warmup4.png"); warmed = true; }
                            ShootQuiet(r, string.Format("g_m{0:000}_s{1:000}_{2}.png", mix, st, a.name));
                            shots++;
                        }
                    }
                    UnityEngine.Object.DestroyImmediate(r.root);
                }
                L(shots + "장 촬영 완료");
            }
            catch (Exception e) { L("예외: " + e); }
            Finish("grid");
        }

        /// <summary>격자는 90장이라 한 장마다 로그를 남기면 읽을 수가 없습니다. 실패할 때만 적습니다.</summary>
        static void ShootQuiet(Rig r, string file)
        {
            var rt = new RenderTexture(Res, Res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();
            bool done = false;
            try
            {
                var req = new UniversalRenderPipeline.SingleCameraRequest();
                req.destination = rt;
                if (RenderPipeline.SupportsRenderRequest(r.cam, req)) { r.cam.SubmitRenderRequest(req); done = true; }
            }
            catch (Exception e) { L("  " + file + " 요청 실패: " + e.Message); }
            if (!done) { r.cam.targetTexture = rt; r.cam.Render(); r.cam.targetTexture = null; }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t2 = new Texture2D(Res, Res, TextureFormat.RGBA32, false, false);
            t2.ReadPixels(new Rect(0, 0, Res, Res), 0, 0); t2.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(Path.Combine(OutDir, file), t2.EncodeToPNG());

            var px = t2.GetPixels32();
            int lit = 0;
            for (int i = 0; i < px.Length; i += 31) if (px[i].r > 8 || px[i].g > 8 || px[i].b > 8) lit++;
            if (lit < 100) L("  !! " + file + " 가 거의 검정입니다 (비검정 표본 " + lit + ")");

            UnityEngine.Object.DestroyImmediate(t2);
            rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }
        static void Finish(string tag)
        {
            string p = Path.Combine(OutDir, "log_" + tag + ".txt");
            File.WriteAllText(p, Log.ToString(), new UTF8Encoding(true));
            Debug.Log("[nt] 로그 저장: " + p);
        }
    }
}