// FBX2NPRParseTest.cs — 관문 시험 (2026-10-08)
// 얼굴 파싱 ONNX(yakhyo/face-parsing, BiSeNet ResNet18, CelebAMask-HQ 19 클래스)가
//   1) 에디트 모드의 Inference Engine(com.unity.ai.inference 2.6.1)에서 임포트·실행되는가,
//   2) 실사 사진이 아니라 '렌더한 게임 캐릭터 머리'에서 쓸 만한 라벨(귀·목·눈썹·입술이 제자리)을 내는가
// 를 본다. 프로젝트 에셋은 바꾸지 않는다(원본 재질 보정은 메모리 사본). 결과는 NormalTestOut/parse/.
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRParseTest.Run
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.InferenceEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRParseTest
    {
        const string ModelPath = "Assets/FBX2NPR/Models/resnet18.onnx";
        const int N = 512;                       // 모델 입력 한 변
        const float FovY = 24f, AmbientLinear = 0.30f;
        // 클래스 순서: yakhyo/face-parsing utils/prepare_labels.py 가 enumerate(attributes, 1) 로 학습 마스크를 만든 순서. 0 = 배경.
        // (CelebAMask-HQ 원본 g_mask.py 의 순서와 다르다 — 검토에서 바로잡음. 첫 실행에서 색칠해 눈으로 확정한다.)
        static readonly string[] Names = { "bg", "skin", "l_brow", "r_brow", "l_eye", "r_eye", "eye_g", "l_ear", "r_ear", "ear_r", "nose", "mouth", "u_lip", "l_lip", "neck", "neck_l", "cloth", "hair", "hat" };
        static readonly Color32[] Palette = {
            new Color32(0, 0, 0, 255),        // bg
            new Color32(230, 180, 150, 255),  // skin
            new Color32(60, 40, 20, 255),     // l_brow
            new Color32(120, 80, 40, 255),    // r_brow
            new Color32(0, 120, 255, 255),    // l_eye
            new Color32(0, 200, 255, 255),    // r_eye
            new Color32(200, 200, 200, 255),  // eye_g
            new Color32(0, 180, 60, 255),     // l_ear
            new Color32(120, 230, 60, 255),   // r_ear
            new Color32(255, 255, 0, 255),    // ear_r
            new Color32(255, 120, 0, 255),    // nose
            new Color32(255, 0, 120, 255),    // mouth
            new Color32(220, 0, 0, 255),      // u_lip
            new Color32(150, 0, 60, 255),     // l_lip
            new Color32(160, 120, 200, 255),  // neck
            new Color32(255, 0, 255, 255),    // neck_l
            new Color32(80, 80, 160, 255),    // cloth
            new Color32(90, 50, 30, 255),     // hair
            new Color32(255, 255, 255, 255),  // hat
        };
        static readonly float[] Mean = { 0.485f, 0.456f, 0.406f }, Std = { 0.229f, 0.224f, 0.225f };   // inference.py 의 ImageNet 정규화

        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "parse"); } }
        static readonly List<string> Log = new List<string>();
        static void L(string s) { Log.Add(s); Debug.Log("[ParseTest] " + s); }
        static string F(double v, string fmt) { return v.ToString(fmt, CultureInfo.InvariantCulture); }

        class Shot { public string name; public Vector3 viewDir; public float margin; public float centerDown; }

        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Log.Clear(); Directory.CreateDirectory(OutDir);
            var temps = new List<UnityEngine.Object>();
            Worker worker = null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                L("환경: Unity " + Application.unityVersion + ", 배치 " + Application.isBatchMode + ", 플레이 " + Application.isPlaying
                  + ", 컴퓨트 셰이더 " + SystemInfo.supportsComputeShaders + ", GPU " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")");

                // 1) 모델 임포트·로드
                var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelPath);
                if (asset == null) { L("모델 에셋을 못 읽음: " + ModelPath + " — ONNX 임포트 실패로 보임. 유니티 로그에서 'unsupported'·'OnnxImportException' 을 찾을 것"); return; }
                Model model;
                try { model = ModelLoader.Load(asset); }
                catch (Exception ex) { L("ModelLoader.Load 실패: " + ex.GetType().Name + " " + ex.Message); return; }
                var sbIn = new StringBuilder(); foreach (var i in model.inputs) sbIn.Append(i.name + " " + i.shape + " " + i.dataType + "; ");
                var sbOut = new StringBuilder(); foreach (var o in model.outputs) sbOut.Append(o.name + " (index " + o.index + "); ");
                L("모델 로드: 입력 " + model.inputs.Count + "개 [" + sbIn + "], 출력 " + model.outputs.Count + "개 [" + sbOut + "], 레이어 " + model.layers.Count + "개");
                if (model.outputs.Count != 1) L("  출력이 하나가 아니다. 첫 출력(feat_out)만 쓴다 (onnx_export.py 가 output_names=['output'] 하나만 이름 붙임)");

                // 2) 워커 (GPUCompute → 실패하면 CPU)
                BackendType backend = BackendType.GPUCompute;
                try { worker = new Worker(model, backend); }
                catch (Exception ex) { L("GPUCompute 워커 생성 실패: " + ex.Message + " → CPU 로"); backend = BackendType.CPU; worker = new Worker(model, backend); }
                L("워커: " + backend);

                // 3) 합성 입력으로 모양·시간 확인 (에디트 모드에서 실제로 도는가)
                {
                    var gray = new byte[N * N * 4]; for (int i = 0; i < gray.Length; i++) gray[i] = 128;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    float[] logits = Infer(worker, gray, out var oshape);
                    sw.Stop();
                    L("합성(회색) 입력: 출력 모양 " + oshape + ", 값 " + logits.Length + "개, 첫 호출 " + sw.ElapsedMilliseconds + "ms (셰이더 컴파일 포함)");
                    sw.Restart(); logits = Infer(worker, gray, out oshape); sw.Stop();
                    L("  둘째 호출 " + sw.ElapsedMilliseconds + "ms");
                    if (oshape.rank != 4 || oshape[1] != Names.Length || oshape[2] != N || oshape[3] != N) { L("  출력 모양이 (1,19,512,512) 가 아니다 — 중단"); return; }
                }

                // 4) 캐릭터
                var shots = new List<Shot> {
                    new Shot { name = "front_tight", viewDir = Vector3.forward, margin = 1.25f, centerDown = 0.00f },
                    new Shot { name = "front_wide",  viewDir = Vector3.forward, margin = 1.80f, centerDown = 0.10f },
                    new Shot { name = "yaw45",       viewDir = FBX2NPRConverter.LightDir(45f, 0f), margin = 1.45f, centerDown = 0.05f },
                    new Shot { name = "yaw90",       viewDir = FBX2NPRConverter.LightDir(90f, 0f), margin = 1.45f, centerDown = 0.05f },
                    new Shot { name = "below20",     viewDir = FBX2NPRConverter.LightDir(0f, -20f), margin = 1.45f, centerDown = 0.05f },
                };
                var only = Environment.GetEnvironmentVariable("FBX2NPR_ONLY");
                foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
                {
                    var modelPath = AssetDatabase.GUIDToAssetPath(g);
                    if (!modelPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                    string c = Path.GetFileNameWithoutExtension(modelPath);
                    if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(';'), c) < 0) continue;
                    L(""); L("==================== " + c);
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(AmbientLinear, AmbientLinear, AmbientLinear, 1f).gamma;   // 색 입력은 감마 값
                    RenderSettings.reflectionIntensity = 0f;
                    var sunGo = new GameObject("Sun"); var sun = sunGo.AddComponent<Light>();
                    sun.type = LightType.Directional; sun.intensity = 1f; sun.color = Color.white; sun.shadows = LightShadows.None;
                    sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.DefaultLight(), Vector3.up);
                    RenderSettings.sun = sun;

                    var imp = AssetImporter.GetAtPath(modelPath);
                    var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
                    var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(modelPath));
                    foreach (var note in OriginalMaterialFix.Apply(pbr, crit, modelPath, temps)) L("  " + note);

                    Bounds head; int headVerts;
                    if (!HeadBounds(pbr, crit, out head, out headVerts)) { L("  머리 본을 못 찾아 건너뜀 (headBoneContains='" + crit.headBoneContains + "')"); continue; }
                    L(string.Format(CultureInfo.InvariantCulture, "  머리 바운드(머리 본 가중치 0.5 이상 정점 {0}개): 중심 ({1:F3}, {2:F3}, {3:F3}) 크기 ({4:F3}, {5:F3}, {6:F3})",
                        headVerts, head.center.x, head.center.y, head.center.z, head.size.x, head.size.y, head.size.z));

                    foreach (var s in shots)
                    {
                        var bb = new Bounds(head.center - Vector3.up * (head.size.y * s.centerDown), head.size);
                        byte[] rgba = Render(bb, s.viewDir, s.margin);                       // 행 0 = 위 (모델 입력 순서)
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        float[] logits = Infer(worker, rgba, out var oshape);
                        sw.Stop();
                        var stats = Analyze(logits, rgba, c + "_" + s.name);
                        L("  [" + s.name + "] " + sw.ElapsedMilliseconds + "ms  " + stats);
                    }
                    UnityEngine.Object.DestroyImmediate(pbr);
                }
                SaveLegend();
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                if (worker != null) worker.Dispose();
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                File.WriteAllText(Path.Combine(OutDir, "log_parse.txt"), string.Join("\n", Log.ToArray()), new UTF8Encoding(true));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static bool HeadBounds(GameObject root, FBX2NPRAssetSettings crit, out Bounds bounds, out int count) { return FBX2NPRParseBake.HeadBounds(root, crit, out bounds, out count); }

        /// <summary>정사각 N×N 으로 찍어 RGBA 바이트(sRGB, 행 0 = 위)로 돌려준다. 구도는 FBX2NPRCompare.Shoot 과 같은 규칙.</summary>
        static byte[] Render(Bounds bb, Vector3 viewDir, float margin)
        {
            var camGo = new GameObject("Cam"); camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.20f, 0.20f, 0.22f);
            cam.fieldOfView = FovY; cam.aspect = 1f; cam.allowHDR = false; cam.allowMSAA = false;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None; acd.dithering = false; acd.renderShadows = false;
            float halfH = Mathf.Max(bb.size.y * 0.5f, bb.size.x * 0.5f);
            float dist = halfH / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * margin + bb.size.z * 0.5f;
            camGo.transform.position = bb.center + viewDir.normalized * dist;
            camGo.transform.rotation = Quaternion.LookRotation(bb.center - camGo.transform.position, Vector3.up);
            float r = bb.extents.magnitude;
            cam.nearClipPlane = Mathf.Max(0.01f, dist - r * 3f); cam.farClipPlane = dist + r * 6f;

            var rt = new RenderTexture(N, N, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); rt.Create();
            bool done = false;
            try { var rq = new UniversalRenderPipeline.SingleCameraRequest(); rq.destination = rt;
                  if (RenderPipeline.SupportsRenderRequest(cam, rq)) { cam.SubmitRenderRequest(rq); done = true; } } catch { }
            if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, N, N), 0, 0); t.Apply(); RenderTexture.active = prev;
            var px = t.GetPixels32();                                  // 행 0 = 아래
            var rgba = new byte[N * N * 4];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                var p = px[(N - 1 - y) * N + x]; int o = (y * N + x) * 4;
                rgba[o] = p.r; rgba[o + 1] = p.g; rgba[o + 2] = p.b; rgba[o + 3] = 255;
            }
            UnityEngine.Object.DestroyImmediate(t); rt.Release(); UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(camGo);
            return rgba;
        }

        /// <summary>RGBA(행 0 = 위) → NCHW 정규화 텐서 → 추론 → 첫 출력의 로짓 [19][N][N]</summary>
        static float[] Infer(Worker worker, byte[] rgba, out TensorShape outShape)
        {
            var data = new float[3 * N * N];
            for (int c = 0; c < 3; c++)
            {
                float m = Mean[c], s = Std[c]; int baseC = c * N * N;
                for (int i = 0; i < N * N; i++) data[baseC + i] = (rgba[i * 4 + c] / 255f - m) / s;
            }
            using (var input = new Tensor<float>(new TensorShape(1, 3, N, N), data))
            {
                worker.Schedule(input);
                var outT = worker.PeekOutput(0) as Tensor<float>;
                outShape = outT.shape;
                using (var cpu = outT.ReadbackAndClone()) return cpu.DownloadToArray();
            }
        }

        /// <summary>소프트맥스·argmax, 클래스별 비율, 평균 최대확률, 구조 검사(눈 위에 눈썹, 눈-코-입 순서). 라벨·겹침 PNG 저장.</summary>
        static string Analyze(float[] logits, byte[] rgba, string tag)
        {
            int K = Names.Length, P = N * N;
            var lab = new byte[P]; var pmax = new float[P]; var count = new int[K]; var cx = new double[K]; var cy = new double[K];
            double sumP = 0; int conf80 = 0;
            var e = new float[K];
            for (int i = 0; i < P; i++)
            {
                float mx = float.NegativeInfinity; int best = 0;
                for (int k = 0; k < K; k++) { float v = logits[k * P + i]; e[k] = v; if (v > mx) { mx = v; best = k; } }
                double z = 0; for (int k = 0; k < K; k++) z += Math.Exp(e[k] - mx);
                float p = (float)(1.0 / z);
                lab[i] = (byte)best; pmax[i] = p; count[best]++; sumP += p; if (p >= 0.8f) conf80++;
                cx[best] += i % N; cy[best] += i / N;
            }
            var labPx = new Color32[P]; var ovl = new Color32[P];
            for (int i = 0; i < P; i++)
            {
                int y = i / N, x = i % N; int o = (N - 1 - y) * N + x;   // PNG 는 행 0 = 아래
                var lc = Palette[lab[i]]; labPx[o] = lc;
                byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
                ovl[o] = new Color32((byte)((r + lc.r) / 2), (byte)((g + lc.g) / 2), (byte)((b + lc.b) / 2), 255);
            }
            SavePng(Path.Combine(OutDir, tag + "_in.png"), rgba, true);
            SavePng(Path.Combine(OutDir, tag + "_lab.png"), labPx);
            SavePng(Path.Combine(OutDir, tag + "_ovl.png"), ovl);

            var sb = new StringBuilder();
            sb.Append("평균 최대확률 " + F(sumP / P, "F3") + ", 0.8 이상 " + F(100.0 * conf80 / P, "F1") + "% | ");
            var order = new List<int>(); for (int k = 0; k < K; k++) if (count[k] > 0) order.Add(k);
            order.Sort((a, b) => count[b].CompareTo(count[a]));
            foreach (var k in order) sb.Append(Names[k] + " " + F(100.0 * count[k] / P, "F1") + "% ");
            // 구조 검사: 화면 y 는 아래로 증가. 눈썹 < 눈 < 코 < 윗입술 이어야 한다 (좌우 눈·눈썹은 합침)
            Func<int[], double> cyOf = ks => { double s = 0; int n = 0; foreach (var k in ks) { s += cy[k]; n += count[k]; } return n > 0 ? s / n : double.NaN; };
            double yBrow = cyOf(new[] { 2, 3 }), yEye = cyOf(new[] { 4, 5 }), yNose = cyOf(new[] { 10 }), yLip = cyOf(new[] { 12 });
            bool ok = !double.IsNaN(yBrow) && !double.IsNaN(yEye) && !double.IsNaN(yNose) && !double.IsNaN(yLip) && yBrow < yEye && yEye < yNose && yNose < yLip;
            sb.Append("| 구조(눈썹<눈<코<윗입술, 화면 y): " + (ok ? "맞음" : "어긋남") + " (" + F(yBrow, "F0") + "/" + F(yEye, "F0") + "/" + F(yNose, "F0") + "/" + F(yLip, "F0") + ")");
            // 귀·목: 있는가
            sb.Append(" | 귀 " + F(100.0 * (count[7] + count[8]) / P, "F2") + "%, 목 " + F(100.0 * count[14] / P, "F2") + "%");
            return sb.ToString();
        }

        static void SavePng(string file, byte[] rgbaTopDown, bool topDown)
        {
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                int i = (y * N + x) * 4; int o = (topDown ? (N - 1 - y) : y) * N + x;
                px[o] = new Color32(rgbaTopDown[i], rgbaTopDown[i + 1], rgbaTopDown[i + 2], 255);
            }
            SavePng(file, px);
        }
        static void SavePng(string file, Color32[] px)
        {
            var t = new Texture2D(N, N, TextureFormat.RGBA32, false, false); t.SetPixels32(px); t.Apply();
            File.WriteAllBytes(file, t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
        }

        static void SaveLegend()
        {
            int w = 260, h = 20 * Names.Length;
            var px = new Color32[w * h];
            for (int k = 0; k < Names.Length; k++)
                for (int y = 0; y < 18; y++) for (int x = 0; x < 40; x++) px[(h - 1 - (k * 20 + y)) * w + x + 2] = Palette[k];
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false); t.SetPixels32(px); t.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, "legend.png"), t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
            var sb = new StringBuilder(); for (int k = 0; k < Names.Length; k++) sb.AppendLine(k + " " + Names[k] + " rgb(" + Palette[k].r + "," + Palette[k].g + "," + Palette[k].b + ")");
            File.WriteAllText(Path.Combine(OutDir, "legend.txt"), sb.ToString(), new UTF8Encoding(true));
        }
    }
}
