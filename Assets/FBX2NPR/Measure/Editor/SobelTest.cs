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
    /// 화면 공간 외곽선을 검증합니다. 확인할 것은 세 가지입니다.
    ///
    ///   1. 카메라 거리를 두 배로 해도 선 두께가 화면상 일정한가.
    ///      메시 띠 방식이 실패한 지점이 정확히 여기입니다. 띠는 월드 공간이라
    ///      멀어지면 가늘어지고 비스듬히 보면 찌그러졌습니다.
    ///   2. 선이 언제나 배경보다 어두운가. 띠는 고정색이라 그늘진 피부 위에서
    ///      오히려 밝게 보여 선이 아니라 긁힌 자국으로 읽혔습니다.
    ///   3. 원본 노멀로 검출하면 평탄화로 지워진 형태가 돌아오는가.
    ///      음영은 평탄화한 노멀로, 선 검출은 원본 노멀로 하는 것이 핵심입니다.
    /// </summary>
    public static class SobelTest
    {
        const string Head = "Assets/NormalTest/Head_mix000.fbx";
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const int Res = 700;
        const float FovY = 24f;

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Debug.Log("[sobel] " + m); Console.WriteLine("[sobel] " + m); }

        [MenuItem("FBX2NPR/화면공간 외곽선 검증")]
        public static void Run()
        {
            Log.Clear();
            try
            {
                var feat = EnsureFeature();
                if (feat == null) { Finish(); return; }

                var mi = AssetImporter.GetAtPath(Head) as ModelImporter;
                mi.importNormals = ModelImporterNormals.Import;
                mi.importTangents = ModelImporterTangents.None;
                mi.weldVertices = false;
                mi.meshOptimizationFlags = 0;
                mi.meshCompression = ModelImporterMeshCompression.Off;
                mi.isReadable = true;
                mi.SaveAndReimport();

                Mesh src = null;
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Head))
                {
                    var m = o as Mesh;
                    if (m != null) { src = m; break; }
                }
                if (src == null) { L("메시 없음"); Finish(); return; }

                var mesh = UnityEngine.Object.Instantiate(src);
                Vector3[] v = mesh.vertices;
                Vector3[] orig = mesh.normals;

                // 원본 노멀을 UV3 에 심어 둡니다. 셰이더의 DepthNormals 패스가 이걸 읽어서
                // 선 검출용 노멀 버퍼에 씁니다. 화면에 칠할 때는 평탄화한 노멀을 쓰므로,
                // 평탄화로 지워진 형태가 정확히 그 자리에 선으로 돌아옵니다.
                mesh.SetUVs(3, new List<Vector3>(orig));

                Vector3 c = RadialNormals.Centroid(v);
                mesh.normals = RadialNormals.Apply(v, orig, c, 1f);
                L(string.Format("메시 정점 {0}, UV3 에 원본 노멀 심음", mesh.vertexCount));

                var mat = new Material(Shader.Find("NormalTest/ToonBand"));
                mat.SetVector("_Band0", new Vector4(0.32f, 0.30f, 0.42f, 1f));
                mat.SetVector("_Band1", new Vector4(0.72f, 0.66f, 0.70f, 1f));
                mat.SetVector("_Band2", new Vector4(1.00f, 0.96f, 0.92f, 1f));
                mat.SetFloat("_Stop1", 0.267f);
                mat.SetFloat("_Stop2", 0.487f);

                float e = 25f * Mathf.Deg2Rad;
                float a = 35f * Mathf.Deg2Rad;
                Vector3 ld = Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a))
                           + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a))
                           + Vector3.up * Mathf.Sin(e);
                mat.SetVector("_LightDir", ld.normalized);

                ShaderUtil.allowAsyncCompilation = false;

                var tags = new string[] { "off", "orig", "flat", "orig_far" };
                var ons = new bool[] { false, true, true, true };
                var uses = new float[] { 1f, 1f, 0f, 1f };
                var dists = new float[] { 1.20f, 1.20f, 1.20f, 2.40f };

                Diagnose(feat);

                // 훑기로 정한 값을 명시적으로 넣습니다.
                // 피처가 에셋에 직렬화돼 있어서 기본값에 기대면 안 됩니다.
                var tuned = feat as SobelOutlineFeature;
                if (tuned != null)
                {
                    tuned.settings.depthScale  = 12f;
                    tuned.settings.normalScale = 0.8f;
                    tuned.settings.threshold   = 0.30f;
                    tuned.settings.thickness   = 1.0f;
                    tuned.settings.darkness    = 0.35f;
                    tuned.settings.debug       = 0f;
                }

                for (int i = 0; i < tags.Length; i++)
                {
                    feat.SetActive(ons[i]);
                    MarkRendererDirty();
                    mat.SetFloat("_UseOrigNormal", uses[i]);
                    SobelOutlineFeature.s_AddCalls = 0;
                    SobelOutlineFeature.s_RecordCalls = 0;
                    SobelOutlineFeature.s_ShaderNull = 0;
                    Shoot(mesh, mat, dists[i], "sob_" + tags[i] + ".png");
                    L(string.Format("      피처 호출 Add={0} Record={1} 셰이더없음={2}  깊이버퍼={3} 노멀버퍼={4}",
                        SobelOutlineFeature.s_AddCalls, SobelOutlineFeature.s_RecordCalls,
                        SobelOutlineFeature.s_ShaderNull,
                        SobelOutlineFeature.s_DepthValid, SobelOutlineFeature.s_NormalsValid));
                }

                // 선이 안 나오면 어느 단계가 비었는지 봅니다.
                var sf = feat as SobelOutlineFeature;
                if (sf != null)
                {
                    mat.SetFloat("_UseOrigNormal", 1f);
                    var dbg = new float[] { 1f, 2f, 3f };
                    var dbgName = new string[] { "edge", "normals", "depth" };
                    for (int i = 0; i < dbg.Length; i++)
                    {
                        sf.settings.debug = dbg[i];
                        MarkRendererDirty();
                        Shoot(mesh, mat, 1.20f, "sob_dbg_" + dbgName[i] + ".png");
                    }
                    sf.settings.debug = 0f;
                    MarkRendererDirty();
                }
                feat.SetActive(true);
                UnityEngine.Object.DestroyImmediate(mesh);

                L("");
                L("판정: orig 와 orig_far 의 선 굵기가 화면상 같아야 합니다.");
                L("      거리가 두 배인데 굵기가 같으면 화면 공간 방식이 맞게 동작한 것입니다.");
            }
            catch (Exception ex) { L("예외: " + ex); }
            Finish();
        }

        /// <summary>
        /// 렌더러 데이터를 무효화해서 URP 가 렌더러를 다시 만들게 합니다.
        /// 이게 없으면 스크립트로 추가한 피처가 이미 만들어진 렌더러에 반영되지 않습니다.
        /// </summary>
        static void MarkRendererDirty()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd == null) return;
            var m = rd.GetType().GetMethod("SetDirty",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (m != null) m.Invoke(rd, null);
        }

        /// <summary>왜 선이 안 나오는지 알아내려고 파이프라인 상태를 찍습니다.</summary>
        static void Diagnose(ScriptableRendererFeature feat)
        {
            L("");
            L("[진단]");
            L("  셰이더 FBX2NPR/SobelOutline : " + (Shader.Find("FBX2NPR/SobelOutline") == null ? "없음" : "있음"));
            L("  셰이더 NormalTest/ToonBand  : " + (Shader.Find("NormalTest/ToonBand") == null ? "없음" : "있음"));

            var qrp = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
            var grp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            var urp = qrp != null ? qrp : grp;
            L("  QualitySettings URP : " + (qrp == null ? "없음" : AssetDatabase.GetAssetPath(qrp)));
            L("  Graphics 기본 URP   : " + (grp == null ? "없음" : AssetDatabase.GetAssetPath(grp)));

            if (urp != null)
            {
                var so = new SerializedObject(urp);
                var list = so.FindProperty("m_RendererDataList");
                if (list != null)
                {
                    for (int i = 0; i < list.arraySize; i++)
                    {
                        var o = list.GetArrayElementAtIndex(i).objectReferenceValue;
                        L(string.Format("    렌더러[{0}] {1}", i,
                            o == null ? "null" : AssetDatabase.GetAssetPath(o)));
                    }
                }
            }

            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd != null)
            {
                L("  " + RendererPath + " 의 피처 " + rd.rendererFeatures.Count + "개");
                foreach (var f in rd.rendererFeatures)
                    L("    - " + (f == null ? "null" : f.name + " (" + f.GetType().Name + ", active=" + f.isActive + ")"));
            }
            L("");
        }

        /// <summary>외곽선 피처가 렌더러 에셋에 없으면 추가합니다.</summary>
        static ScriptableRendererFeature EnsureFeature()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd == null) { L("렌더러 에셋을 찾지 못함: " + RendererPath); return null; }

            foreach (var f in rd.rendererFeatures)
            {
                if (f is SobelOutlineFeature) { L("외곽선 피처가 이미 등록되어 있습니다"); return f; }
            }

            var nf = ScriptableObject.CreateInstance<SobelOutlineFeature>();
            nf.name = "FBX2NPR Sobel Outline";
            rd.rendererFeatures.Add(nf);
            AssetDatabase.AddObjectToAsset(nf, rd);
            EditorUtility.SetDirty(rd);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            L("외곽선 피처를 렌더러 에셋에 등록했습니다");
            return nf;
        }

        static void Shoot(Mesh mesh, Material mat, float distK, string file)
        {
            var root = new GameObject("SobelRig");
            root.hideFlags = HideFlags.HideAndDontSave;

            var go = new GameObject("Head");
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;

            Bounds b = mesh.bounds;
            float r = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) * 0.5f;
            float dist = r / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * distK;

            var camGo = new GameObject("Cam");
            camGo.transform.SetParent(root.transform, false);
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.fieldOfView = FovY;
            cam.aspect = 1f;
            cam.nearClipPlane = Mathf.Max(0.001f, dist - r * 3f);
            cam.farClipPlane = dist + r * 8f;
            cam.allowHDR = false;
            cam.allowMSAA = false;

            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false;
            acd.antialiasing = AntialiasingMode.None;
            acd.dithering = false;
            acd.renderShadows = false;

            camGo.transform.position = b.center + Vector3.right * dist;
            camGo.transform.rotation = Quaternion.LookRotation(b.center - camGo.transform.position, Vector3.up);

            var rt = new RenderTexture(Res, Res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.Create();

            bool done = false;
            try
            {
                var req = new UniversalRenderPipeline.SingleCameraRequest();
                req.destination = rt;
                if (RenderPipeline.SupportsRenderRequest(cam, req)) { cam.SubmitRenderRequest(req); done = true; }
            }
            catch (Exception ex) { L("  렌더 요청 실패: " + ex.Message); }
            if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(Res, Res, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, Res, Res), 0, 0);
            t.Apply();
            RenderTexture.active = prev;

            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, file), t.EncodeToPNG());

            // 전경 대비 어두운 화소 수로 선의 양을 대충 잽니다.
            // 정확한 두께 판정은 렌더 이미지를 눈과 별도 계측으로 봅니다.
            var px = t.GetPixels32();
            int lit = 0, dark = 0;
            // 가장 어두운 밴드의 밝기가 158 정도이므로, 그보다 확실히 어두운 화소만 셉니다.
            for (int i = 0; i < px.Length; i++)
            {
                int s = (px[i].r + px[i].g + px[i].b) / 3;
                if (s <= 8) continue;
                lit++;
                if (s < 140) dark++;
            }
            L(string.Format(CultureInfo.InvariantCulture,
                "  {0,-16} 거리배율 {1:F2}  전경표본 {2,6}  어두운표본 {3,5}", file, distK, lit, dark));

            UnityEngine.Object.DestroyImmediate(t);
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(root);
        }

        static void Finish()
        {
            string d = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "log_sobel.txt"), Log.ToString(), new UTF8Encoding(true));
        }
    }
}
