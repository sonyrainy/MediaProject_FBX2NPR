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
    /// 사용자의 관찰을 검증합니다.
    /// "텍스처 없는 클레이 + 밴드 + 선은 완성도가 높은데, 사진 알베도를 입히면 같은 선이 어색하다."
    ///
    /// 같은 메시 · 같은 카메라 · 같은 광원 · 같은 선 설정으로 알베도 쪽만 바꿉니다.
    /// 알베도가 원인이라면 알베도를 전처리했을 때 선이 다시 살아나야 합니다.
    /// 지오메트리가 원인이라면 알베도를 아무리 손봐도 안 살아납니다.
    /// 이 둘을 가르는 것이 목적입니다.
    /// </summary>
    public static class AlbedoLineTest
    {
        const string Head = "Assets/NormalTest/Head_mix000.fbx";
        const string AlbRaw = "Assets/NormalTest/Head_Albedo.png";
        const string AlbFlat = "Assets/NormalTest/Head_Albedo_Flat.png";
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const int Res = 700;
        const float FovY = 24f;

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Console.WriteLine("[alb] " + m); }

        class Case
        {
            public string tag;
            public bool lines;
            public float albedoOn;
            public bool useFlat;
            public float posterize;
            public float posterizeLum;
            public float flatten;
            public float flattenMip;
            public float saturation;
            public float lift;
            public string desc;
        }

        [MenuItem("FBX2NPR/알베도와 선 상호작용 검증")]
        public static void Run()
        {
            Log.Clear();
            try
            {
                SobelOutlineFeature feat = null;
                var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
                if (rd != null)
                    foreach (var f in rd.rendererFeatures) { feat = f as SobelOutlineFeature; if (feat != null) break; }
                if (feat == null) { L("외곽선 피처 없음. SobelTest 를 먼저 실행하세요."); Finish(); return; }

                // 확정된 튜닝값. 이 실험 내내 고정합니다.
                feat.settings.depthScale = 12f;
                feat.settings.normalScale = 0.8f;
                feat.settings.threshold = 0.30f;
                feat.settings.thickness = 1.0f;
                feat.settings.darkness = 0.35f;
                feat.settings.debug = 0f;

                FixTexture(AlbRaw);
                FixTexture(AlbFlat);
                var texRaw = AssetDatabase.LoadAssetAtPath<Texture2D>(AlbRaw);
                var texFlat = AssetDatabase.LoadAssetAtPath<Texture2D>(AlbFlat);
                L("알베도 원본 " + (texRaw == null ? "없음" : texRaw.width + "x" + texRaw.height));
                L("알베도 디베이크 " + (texFlat == null ? "없음" : texFlat.width + "x" + texFlat.height));

                var mi = AssetImporter.GetAtPath(Head) as ModelImporter;
                mi.importNormals = ModelImporterNormals.Import;
                mi.importTangents = ModelImporterTangents.CalculateMikk;
                mi.weldVertices = false;
                mi.meshOptimizationFlags = 0;
                mi.meshCompression = ModelImporterMeshCompression.Off;
                mi.isReadable = true;
                mi.SaveAndReimport();

                Mesh src = null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Head)) { var m = o as Mesh; if (m != null) { src = m; break; } }
                if (src == null) { L("메시 없음"); Finish(); return; }

                var mesh = UnityEngine.Object.Instantiate(src);
                var v = mesh.vertices;
                var orig = mesh.normals;
                mesh.SetUVs(3, new List<Vector3>(orig));
                mesh.normals = RadialNormals.Apply(v, orig, RadialNormals.Centroid(v), 1f);
                L("메시 정점 " + mesh.vertexCount + ", UV 채널0 존재 " + (mesh.uv != null && mesh.uv.Length > 0));

                var mat = new Material(Shader.Find("NormalTest/ToonBand"));
                mat.SetVector("_Band0", new Vector4(0.32f, 0.30f, 0.42f, 1f));
                mat.SetVector("_Band1", new Vector4(0.72f, 0.66f, 0.70f, 1f));
                mat.SetVector("_Band2", new Vector4(1.00f, 0.96f, 0.92f, 1f));
                mat.SetFloat("_Stop1", 0.267f);
                mat.SetFloat("_Stop2", 0.487f);
                mat.SetFloat("_UseOrigNormal", 1f);
                mat.SetFloat("_NormalStrength", 0f);
                float e = 25f * Mathf.Deg2Rad, a = 35f * Mathf.Deg2Rad;
                mat.SetVector("_LightDir", (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a))
                    + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a)) + Vector3.up * Mathf.Sin(e)).normalized);

                ShaderUtil.allowAsyncCompilation = false;

                var cases = new Case[]
                {
                    new Case { tag="a0_clay_noline",   lines=false, albedoOn=0f, desc="클레이, 선 없음" },
                    new Case { tag="a1_clay_line",     lines=true,  albedoOn=0f, desc="클레이 + 선  (사용자가 좋다고 한 것)" },
                    new Case { tag="a2_raw_noline",    lines=false, albedoOn=1f, useFlat=false, saturation=1f, desc="사진 알베도, 선 없음" },
                    new Case { tag="a3_raw_line",      lines=true,  albedoOn=1f, useFlat=false, saturation=1f, desc="사진 알베도 + 선  (사용자가 어색하다고 한 것)" },
                    new Case { tag="a4_post5_line",    lines=true,  albedoOn=1f, useFlat=false, saturation=1f,    posterize=5f, desc="알베도 5단 계단화 + 선" },
                    new Case { tag="a5_post5sl_line",  lines=true,  albedoOn=1f, useFlat=false, saturation=0.7f,  posterize=5f, lift=0.18f, desc="계단화 + 채도0.7 + 밝기들어올림 + 선" },
                    new Case { tag="a6_half_line",     lines=true,  albedoOn=0.5f, useFlat=false, saturation=0.7f, posterize=5f, lift=0.18f, desc="위와 같되 알베도를 절반만 섞음 + 선" },
                    new Case { tag="a7_flat_line",     lines=true,  albedoOn=1f, useFlat=true,  saturation=1f, desc="디베이크 알베도 + 선" },
                    new Case { tag="a8_flatpost_line", lines=true,  albedoOn=1f, useFlat=true,  saturation=0.7f, posterize=5f, lift=0.18f, desc="디베이크 + 계단화 + 채도0.7 + 선" },

                    // 1차 결과: 채널별 계단화가 색을 무지개로 만들었고, 알베도 비중을 낮춘 것만 효과가 있었다.
                    // 그래서 (가) 고주파 제거, (나) 밝기만 계단화, (다) 비중 낮추기를 분리해서 잰다.
                    new Case { tag="b0_flat40",        lines=true, albedoOn=1f,   useFlat=false, saturation=1f,    flatten=0.40f, flattenMip=4f, desc="디테일 40% 제거" },
                    new Case { tag="b1_flat70",        lines=true, albedoOn=1f,   useFlat=false, saturation=1f,    flatten=0.70f, flattenMip=4f, desc="디테일 70% 제거" },
                    new Case { tag="b2_flat90mip5",    lines=true, albedoOn=1f,   useFlat=false, saturation=1f,    flatten=0.90f, flattenMip=5f, desc="디테일 90% 제거 (밉5)" },
                    new Case { tag="b3_flat70_lum4",   lines=true, albedoOn=1f,   useFlat=false, saturation=1f,    flatten=0.70f, flattenMip=4f, posterizeLum=4f, desc="디테일70% + 밝기만 4단 계단화" },
                    new Case { tag="b4_flat70_lum4_s",  lines=true, albedoOn=1f,  useFlat=false, saturation=0.75f, flatten=0.70f, flattenMip=4f, posterizeLum=4f, lift=0.12f, desc="위 + 채도0.75 + 밝기↑" },
                    new Case { tag="b5_mix65",         lines=true, albedoOn=0.65f, useFlat=false, saturation=1f,   flatten=0.70f, flattenMip=4f, desc="디테일70% + 알베도 비중 0.65" },
                    new Case { tag="b6_mix65_lum4",    lines=true, albedoOn=0.65f, useFlat=false, saturation=0.8f, flatten=0.70f, flattenMip=4f, posterizeLum=4f, lift=0.10f, desc="종합: 디테일70%·비중0.65·밝기4단·채도0.8" },
                    new Case { tag="b7_mix65_noline",  lines=false, albedoOn=0.65f, useFlat=false, saturation=0.8f, flatten=0.70f, flattenMip=4f, posterizeLum=4f, lift=0.10f, desc="위와 같되 선 없음 (대조군)" },
                };

                L("");
                L("case                  선   알베도  계단  채도  들어올림   정면 어두운화소  측면 어두운화소");
                foreach (var c in cases)
                {
                    feat.SetActive(c.lines);
                    Dirty();
                    mat.SetFloat("_AlbedoOn", c.albedoOn);
                    mat.SetTexture("_Albedo", c.useFlat ? texFlat : texRaw);
                    mat.SetFloat("_Posterize", c.posterize);
                    mat.SetFloat("_PosterizeLum", c.posterizeLum);
                    mat.SetFloat("_Flatten", c.flatten);
                    mat.SetFloat("_FlattenMip", c.flattenMip <= 0f ? 4f : c.flattenMip);
                    mat.SetFloat("_Saturation", c.saturation <= 0f ? 1f : c.saturation);
                    mat.SetFloat("_Lift", c.lift);

                    int df = Shoot(mesh, mat, Vector3.forward, c.tag + "_front.png");
                    int ds = Shoot(mesh, mat, Vector3.right, c.tag + "_side.png");
                    L(string.Format(CultureInfo.InvariantCulture,
                        "{0,-18} {1,-4} {2,5:F2} {3,5:F0} {4,5:F2} {5,7:F2}   {6,10}  {7,10}   {8}",
                        c.tag, c.lines ? "ON" : "off", c.albedoOn, c.posterize,
                        c.saturation <= 0f ? 1f : c.saturation, c.lift, df, ds, c.desc));
                }

                feat.SetActive(true);
                Dirty();
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            catch (Exception ex) { L("예외: " + ex); }
            Finish();
        }

        static void FixTexture(string path)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) { L("텍스처 임포터 없음: " + path); return; }
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.mipmapEnabled = true;
            ti.maxTextureSize = 2048;          // 실험 속도용. 화질 판단에는 충분합니다.
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.SaveAndReimport();
        }

        static void Dirty()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd == null) return;
            var m = rd.GetType().GetMethod("SetDirty", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (m != null) m.Invoke(rd, null);
        }

        static int Shoot(Mesh mesh, Material mat, Vector3 dir, string file)
        {
            var root = new GameObject("Rig"); root.hideFlags = HideFlags.HideAndDontSave;
            var go = new GameObject("Head"); go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            Bounds b = mesh.bounds;
            float r = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 0.5f;
            float dist = r / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * 1.20f;

            var camGo = new GameObject("Cam"); camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
            cam.fieldOfView = FovY; cam.aspect = 1f;
            cam.nearClipPlane = Mathf.Max(0.001f, dist - r * 3f);
            cam.farClipPlane = dist + r * 8f;
            cam.allowHDR = false; cam.allowMSAA = false;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None;
            acd.dithering = false; acd.renderShadows = false;
            camGo.transform.position = b.center + dir * dist;
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

            string dir2 = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(dir2);
            File.WriteAllBytes(Path.Combine(dir2, file), t.EncodeToPNG());

            // 알베도가 들어가면 밝기 분포가 통째로 달라지므로 절대 문턱은 의미가 없습니다.
            // 각 이미지의 전경 중앙값 대비 60% 미만인 화소를 "어두운 것"으로 셉니다.
            var px = t.GetPixels32();
            var fg = new List<int>();
            for (int i = 0; i < px.Length; i++)
            {
                int s = (px[i].r + px[i].g + px[i].b) / 3;
                if (s > 8) fg.Add(s);
            }
            int dark = 0;
            if (fg.Count > 0)
            {
                fg.Sort();
                int med = fg[fg.Count / 2];
                int cut = Mathf.RoundToInt(med * 0.60f);
                foreach (var s in fg) if (s < cut) dark++;
            }

            UnityEngine.Object.DestroyImmediate(t); rt.Release();
            UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(root);
            return dark;
        }

        static void Finish()
        {
            string d = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "log_albedo.txt"), Log.ToString(), new UTF8Encoding(true));
        }
    }
}
