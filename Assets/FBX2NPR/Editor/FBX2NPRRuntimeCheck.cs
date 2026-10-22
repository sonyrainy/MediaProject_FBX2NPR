// FBX2NPRRuntimeCheck.cs — 게임 적용 검증. (1) 툰·회화풍 프리팹을 한 씬에 놓고 걷기 흉내 + 캡처 (플레이 모드·빌드 공용 드라이버 RuntimeCheckDriver)
// (2) 텍스처 압축 A/B (3) 윈도우 빌드. 배치: runplay.ps1 (플레이 모드, -quit 없이) / rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRRuntimeCheck.CompressionCheck / ...Build
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using FBX2NPR.RuntimeCheck;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRRuntimeCheck
    {
        const string SceneDir = "Assets/FBX2NPRRuntimeCheck";
        public const string ScenePath = SceneDir + "/RuntimeCheck.unity";
        const string GroundMatPath = SceneDir + "/Ground.mat";
        const string OutlineShaderPath = "Assets/FBX2NPR/SobelOutline.shader";
        const float AmbientLinear = 0.30f;
        static string ProjectRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }
        public static string OutRoot { get { return Path.Combine(ProjectRoot, "NormalTestOut", "runtime"); } }
        // 씬에 놓을 프리팹 (화면 왼쪽부터). 회화풍을 먼저 만들고 툰을 나중에 만든다 → 씬 로드 때 OnEnable 이 나중인 툰 리그 값이 외곽선 피처에 남는다 (드라이버가 기록).
        static readonly string[] DefaultCast = { "TheBoss_Painterly", "Jones_Painterly", "Ch07_Painterly", "Ch07_Toon", "TheBoss_Toon", "unitychan_Toon" };
        static readonly string[] DefaultFaceCams = { "Ch07_Toon", "Ch07_Painterly", "TheBoss_Toon" };
        // 환경변수로 출연진을 바꿀 수 있다 (세미콜론 구분): FBX2NPR_RC_CAST, FBX2NPR_RC_FACECAMS — 결과표용으로 기본 씬에 없는 프리팹(Jones_Toon, unitychan_Painterly)을 돌릴 때 (기록 98번)
        static string[] EnvList(string key, string[] def)
        {
            var v = Environment.GetEnvironmentVariable(key); if (string.IsNullOrEmpty(v)) return def;
            var o = new List<string>(); foreach (var s in v.Split(';')) { var t = s.Trim(); if (t.Length > 0) o.Add(t); }
            return o.Count > 0 ? o.ToArray() : def;
        }
        static string[] Cast { get { return EnvList("FBX2NPR_RC_CAST", DefaultCast); } }
        static string[] FaceCams { get { return EnvList("FBX2NPR_RC_FACECAMS", DefaultFaceCams); } }

        internal static string Model(string prefabName) { return prefabName.Substring(0, prefabName.LastIndexOf('_')); }
        internal static string PrefabPath(string prefabName) { string m = FBX2NPRConverter.Safe(Model(prefabName)); return FBX2NPRConverter.OutRoot + "/" + m + "/" + prefabName + ".prefab"; }
        static string FindModel(string name)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }
        internal static FBX2NPRAssetSettings Crit(string model)
        {
            string p = FindModel(model); var imp = p != null ? AssetImporter.GetAtPath(p) : null;
            return FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
        }

        internal static void NewStage(bool shadows)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(AmbientLinear, AmbientLinear, AmbientLinear, 1f).gamma; RenderSettings.reflectionIntensity = 0f;
            var sunGo = new GameObject("Sun"); var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional; sun.intensity = 1f; sun.color = Color.white; sun.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.LightDir(35f, 25f), Vector3.up); RenderSettings.sun = sun;
        }

        internal static Bounds RendererBounds(GameObject go)
        {
            var b = new Bounds(); bool init = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { if (!init) { b = r.bounds; init = true; } else b.Encapsulate(r.bounds); }
            return b;
        }

        /// <summary>보이는 메시의 가장 낮은 정점의 월드 y (스킨 메시는 지금 자세로 굽는다). 렌더러 경계는 여유가 붙어 바닥 기준으로 쓸 수 없다.</summary>
        internal static float MeshMinY(GameObject go)
        {
            float minY = float.MaxValue; var bm = new Mesh();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                Mesh m = null; var smr = r as SkinnedMeshRenderer;
                if (smr != null) { if (smr.sharedMesh == null) continue; smr.BakeMesh(bm, true); m = bm; }
                else { var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue; m = mf.sharedMesh; }
                var v = m.vertices; var tr = r.transform;
                for (int i = 0; i < v.Length; i++) { float y = tr.TransformPoint(v[i]).y; if (y < minY) minY = y; }
            }
            UnityEngine.Object.DestroyImmediate(bm);
            return minY == float.MaxValue ? RendererBounds(go).min.y : minY;
        }

        internal static Bounds HeadOf(GameObject go, string model, List<string> notes)
        {
            var crit = Crit(model); Bounds head; int hv;
            if (FBX2NPRParseBake.HeadBounds(go, crit, out head, out hv)) return head;
            var hb = FBX2NPRConverter.FindHeadBone(go.transform, crit.headBoneContains);
            var body = RendererBounds(go);
            head = new Bounds(hb != null ? hb.position + Vector3.up * 0.08f : new Vector3(body.center.x, body.max.y - 0.12f, body.center.z), Vector3.one * 0.28f);
            notes.Add("머리 바운드 실패 → 머리 본 기준 0.28m 상자: " + go.name);
            return head;
        }

        static Camera MakeCam(string name, float fov, bool enabled)
        {
            var go = new GameObject(name); var cam = go.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.20f, 0.20f, 0.22f);
            cam.fieldOfView = fov; cam.nearClipPlane = 0.05f; cam.farClipPlane = 60f; cam.allowHDR = false; cam.allowMSAA = false; cam.enabled = enabled;
            var acd = go.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None; acd.dithering = false; acd.renderShadows = true;
            return cam;
        }

        [MenuItem("FBX2NPR/게임 적용 검증/검증 씬 만들기")]
        public static string BuildScene()
        {
            var notes = new List<string>();
            NewStage(true);
            var scene = SceneManager.GetActiveScene();
            FBX2NPRConverter.EnsureFolder(SceneDir);
            // 바닥 (씬은 재질을 품을 수 없어 에셋으로)
            var groundMat = AssetDatabase.LoadAssetAtPath<Material>(GroundMatPath);
            if (groundMat == null)
            {
                groundMat = new Material(Shader.Find("Universal Render Pipeline/Lit")); groundMat.SetColor("_BaseColor", new Color(0.42f, 0.42f, 0.44f)); groundMat.SetFloat("_Smoothness", 0.1f);
                AssetDatabase.CreateAsset(groundMat, GroundMatPath);
            }
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane); ground.name = "Ground"; ground.transform.localScale = new Vector3(3f, 1f, 3f); ground.GetComponent<Renderer>().sharedMaterial = groundMat;
            // 캐릭터
            var roots = new Dictionary<string, GameObject>(); var order = new List<string>(); var walkers = new List<ProceduralWalker>();
            var toonRigs = new List<ToonFaceRig>(); var paintRigs = new List<PainterlyRig>(); var animators = new List<Animator>();
            // 실제 클립이 있으면(FBX2NPRHumanoidSetup 이 만든 컨트롤러) Animator 로, 없으면 걷기 흉내로. 환경변수 FBX2NPR_RC_PROCEDURAL=1 이면 강제로 걷기 흉내
            // FBX2NPR_RC_CONTROLLER 가 있으면 그 컨트롤러로 (기록 98번: 왼발 꼬임 수정안 미리보기 — 실제 클립 설정은 그대로 두고 사본 클립 컨트롤러로 찍어 본다)
            var ctrlOverride = Environment.GetEnvironmentVariable("FBX2NPR_RC_CONTROLLER");
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(string.IsNullOrEmpty(ctrlOverride) ? FBX2NPRHumanoidSetup.ControllerPath : ctrlOverride);
            if (!string.IsNullOrEmpty(ctrlOverride)) notes.Add("컨트롤러 덮어쓰기: " + ctrlOverride + (ctrl != null ? "" : " (없음 → 클립 없이)"));
            bool useClips = ctrl != null && Environment.GetEnvironmentVariable("FBX2NPR_RC_PROCEDURAL") != "1";
            var stateNames = new List<string>(); if (useClips) foreach (var c in FBX2NPRHumanoidSetup.Clips()) stateNames.Add(c.name);
            notes.Add(useClips ? "애니메이션: 클립 " + stateNames.Count + "개 (" + string.Join(", ", stateNames.ToArray()) + "), Humanoid 리타깃" : "애니메이션: 걷기 흉내 (클립 없음)");
            foreach (var n in Cast)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(n));
                if (prefab == null) { notes.Add("프리팹 없음, 건너뜀: " + PrefabPath(n)); continue; }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab); go.name = n;
                roots[n] = go; order.Add(n);
                if (useClips)
                {
                    var an = go.GetComponent<Animator>(); if (an == null) an = go.AddComponent<Animator>();
                    if (an.avatar == null) { var mp = FBX2NPRHumanoidSetup.ModelPath(Model(n)); if (mp != null) an.avatar = FBX2NPRHumanoidSetup.AvatarOf(mp); }
                    an.runtimeAnimatorController = ctrl; an.applyRootMotion = false; an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animators.Add(an);
                    notes.Add(n + ": Animator 아바타 " + (an.avatar != null ? an.avatar.name + " (사람형 " + an.avatar.isHuman + ")" : "없음"));
                }
                else { var w = go.AddComponent<ProceduralWalker>(); w.phase = order.Count * 1.3f; walkers.Add(w); }
                toonRigs.AddRange(go.GetComponentsInChildren<ToonFaceRig>(true)); paintRigs.AddRange(go.GetComponentsInChildren<PainterlyRig>(true));
            }
            // 가로 배치: 키의 0.55배(최소 0.9m) 간격, 발을 y=0 에. 카메라는 +z 에서 -z 를 보므로 화면 왼쪽이 월드 +x.
            float maxH = 0f; var bounds = new Dictionary<string, Bounds>();
            foreach (var n in order) { var b = RendererBounds(roots[n]); bounds[n] = b; maxH = Mathf.Max(maxH, b.size.y); }
            float gap = Mathf.Max(0.9f, maxH * 0.55f), total = gap * Mathf.Max(0, order.Count - 1);
            for (int i = 0; i < order.Count; i++)
            {
                var b = bounds[order[i]]; var go = roots[order[i]];
                // 바닥은 실제 메시의 가장 낮은 정점으로 맞춘다. 스킨 메시의 렌더러 경계는 발바닥보다 4~13cm 아래까지 잡혀 있어서,
                // 그 바닥에 맞추면 캐릭터가 그만큼 떠서 걷고 그림자가 발에서 떨어져 보였다 (2026-10-14, -contactLog 측정)
                float floorY = MeshMinY(go);
                go.transform.position = new Vector3(total * 0.5f - gap * i - b.center.x, -floorY, -b.center.z);
                notes.Add(string.Format("{0}: 키 {1:F2}m, 폭(T포즈) {2:F2}m, x={3:F2}, 바닥 메시 최저점 {4:F3}m (렌더러 경계 바닥 {5:F3}m)", order[i], b.size.y, b.size.x, go.transform.position.x, floorY, b.min.y));
            }
            // 넓은 카메라 (16:9, 세로 FOV 30) — 줄 전체와 키가 들어오게
            var wide = MakeCam("WideCam", 30f, true); wide.tag = "MainCamera";
            float aspect = 1280f / 720f, fovX = 2f * Mathf.Atan(Mathf.Tan(15f * Mathf.Deg2Rad) * aspect);
            float halfW = total * 0.5f + 0.7f, halfH = maxH * 0.5f + 0.25f;
            float dist = Mathf.Max(halfW / Mathf.Tan(fovX * 0.5f), halfH / Mathf.Tan(15f * Mathf.Deg2Rad));
            wide.transform.position = new Vector3(0f, maxH * 0.55f, dist); wide.transform.LookAt(new Vector3(0f, maxH * 0.5f, 0f));
            var cams = new List<Camera> { wide }; var camNames = new List<string> { "wide" }; var camW = new List<int> { 1280 }; var camH = new List<int> { 720 }; var follow = new List<Transform> { null };
            notes.Add(string.Format("넓은 카메라: 간격 {0:F2}m, 줄 폭 {1:F2}m, 최대 키 {2:F2}m, 거리 {3:F2}m", gap, total, maxH, dist));
            // 얼굴 카메라 (정사각, 세로 FOV 24): 머리 바운드가 화면의 80% 쯤
            foreach (var fc in FaceCams)
            {
                GameObject go; if (!roots.TryGetValue(fc, out go)) continue;
                var head = HeadOf(go, Model(fc), notes);
                var cam = MakeCam("FaceCam_" + fc, 24f, false);
                float hh = Mathf.Max(head.size.y, head.size.x) * 0.5f * 1.25f;
                float d = hh / Mathf.Tan(12f * Mathf.Deg2Rad) + head.size.z * 0.5f;
                cam.transform.position = head.center + new Vector3(0f, 0f, d); cam.transform.LookAt(head.center);
                cams.Add(cam); camNames.Add("face_" + fc); camW.Add(512); camH.Add(512);
                follow.Add(FBX2NPRConverter.FindHeadBone(go.transform, Crit(Model(fc)).headBoneContains));   // 애니메이션 중 머리를 따라간다
                notes.Add(string.Format("얼굴 카메라 {0}: 머리 중심 ({1:F2},{2:F2},{3:F2}) 크기 {4:F2}x{5:F2}, 거리 {6:F2}", fc, head.center.x, head.center.y, head.center.z, head.size.x, head.size.y, d));
            }
            // 드라이버
            notes.Add("출연: " + string.Join(", ", order.ToArray()) + " / 얼굴 카메라: " + string.Join(", ", FaceCams));
            var drvGo = new GameObject("RuntimeCheckDriver"); var drv = drvGo.AddComponent<RuntimeCheckDriver>();
            drv.cams = cams.ToArray(); drv.camNames = camNames.ToArray(); drv.camW = camW.ToArray(); drv.camH = camH.ToArray();
            drv.walkers = walkers.ToArray(); drv.toonRigs = toonRigs.ToArray(); drv.paintRigs = paintRigs.ToArray();
            drv.animators = animators.ToArray(); drv.states = stateNames.ToArray(); drv.follow = follow.ToArray();
            EditorSceneManager.SaveScene(scene, ScenePath);
            Directory.CreateDirectory(OutRoot);
            File.WriteAllText(Path.Combine(OutRoot, "scene_notes.txt"), string.Join("\n", notes.ToArray()), new UTF8Encoding(true));
            foreach (var n in notes) Debug.Log("[RuntimeCheck] " + n);
            return ScenePath;
        }

        /// <summary>배치: 씬을 만들고 플레이 모드로 들어간다. 드라이버가 끝나면 ExitPlaymode → 아래 FBX2NPRRuntimeCheckExit 가 에디터를 끝낸다 (-quit 없이 띄울 것).</summary>
        [MenuItem("FBX2NPR/게임 적용 검증/플레이 모드 검증")]
        public static void Play()
        {
            BuildScene();
            SessionState.SetBool("FBX2NPRRC.exitAfterPlay", Application.isBatchMode);
            Debug.Log("[RuntimeCheck] 플레이 모드 진입 (featureMode=" + (Environment.GetEnvironmentVariable("FBX2NPR_RC_FEATURE") ?? "toon") + ")");
            EditorApplication.EnterPlaymode();
        }

        class Variant { public string name; public TextureImporterCompression comp; public bool crunch; }
        class Saved { public string path; public TextureImporterCompression comp; public bool crunch; public int quality; }

        /// <summary>도구가 만든 텍스처의 압축 포맷을 바꿔 가며(BC7 현재 / 무압축 / DXT / DXT+크런치) 같은 구도로 찍고 무압축 대비 차이를 잰다. 끝나면 임포트 설정을 되돌린다.</summary>
        public static void CompressionCheck()
        {
            string outDir = Path.Combine(OutRoot, "compress"); Directory.CreateDirectory(outDir);
            var log = new List<string>();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            var saved = new List<Saved>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                string[] targets = { "Ch07_Toon", "TheBoss_Toon", "Ch07_Painterly", "TheBoss_Painterly" };
                var variants = new[] {
                    new Variant { name = "bc7", comp = TextureImporterCompression.CompressedHQ, crunch = false },   // 현재 설정 (도구가 CompressedHQ 로 저장)
                    new Variant { name = "raw", comp = TextureImporterCompression.Uncompressed, crunch = false },
                    new Variant { name = "dxt", comp = TextureImporterCompression.Compressed, crunch = false },
                    new Variant { name = "crunch", comp = TextureImporterCompression.Compressed, crunch = true } };
                foreach (var tname in targets)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(tname));
                    if (prefab == null) { log.Add("프리팹 없음: " + tname); continue; }
                    // 이 프리팹의 재질이 쓰는 텍스처 중 도구가 만든 것(Assets/FBX2NPRConverted 아래)만 바꾼다. 원본 텍스처(TestSet)는 그대로 두고 포맷만 적는다.
                    var gen = new List<string>(); var orig = new List<string>();
                    foreach (var r in prefab.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.sharedMaterials)
                        {
                            if (m == null) continue;
                            foreach (var pn in m.GetTexturePropertyNames())
                            {
                                var t = m.GetTexture(pn) as Texture2D; if (t == null) continue;
                                string p = AssetDatabase.GetAssetPath(t); if (string.IsNullOrEmpty(p)) continue;
                                if (p.StartsWith(FBX2NPRConverter.OutRoot)) { if (!gen.Contains(p)) gen.Add(p); } else if (!orig.Contains(p)) orig.Add(p);
                            }
                        }
                    log.Add(""); log.Add("==================== " + tname + ": 도구 생성 텍스처 " + gen.Count + ", 원본 텍스처 " + orig.Count);
                    foreach (var p in orig) { var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p); if (t != null) log.Add(string.Format("  원본 {0}: {1} {2}x{3} {4:F1}MB", Path.GetFileName(p), t.format, t.width, t.height, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t) / 1048576.0)); }
                    foreach (var p in gen)
                    {
                        var ti = AssetImporter.GetAtPath(p) as TextureImporter; if (ti == null) continue;
                        if (!saved.Exists(s => s.path == p)) saved.Add(new Saved { path = p, comp = ti.textureCompression, crunch = ti.crunchedCompression, quality = ti.compressionQuality });
                        var ps = ti.GetPlatformTextureSettings("Standalone");
                        log.Add(string.Format("  생성 {0}: 기본 {1}, Standalone 오버라이드 {2}({3}), 자동 포맷 {4}", Path.GetFileName(p), ti.textureCompression, ps.overridden, ps.textureCompression, ti.GetAutomaticFormat("Standalone")));
                    }
                    var shots = new Dictionary<string, string>();
                    foreach (var v in variants)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        foreach (var p in gen)
                        {
                            var ti = AssetImporter.GetAtPath(p) as TextureImporter; if (ti == null) continue;
                            if (ti.textureCompression == v.comp && ti.crunchedCompression == v.crunch) continue;
                            ti.textureCompression = v.comp; ti.crunchedCompression = v.crunch; ti.compressionQuality = 50; ti.SaveAndReimport();
                        }
                        sw.Stop();
                        var fm = new StringBuilder();
                        foreach (var p in gen) { var t = AssetDatabase.LoadAssetAtPath<Texture2D>(p); if (t != null) fm.Append(string.Format("{0} {1} {2:F1}MB; ", Path.GetFileName(p), t.format, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t) / 1048576.0)); }
                        log.Add(string.Format("  [{0}] 임포트 {1:F0}초: {2}", v.name, sw.Elapsed.TotalSeconds, fm));
                        string face = Path.Combine(outDir, tname + "_" + v.name + "_face.png"), body = Path.Combine(outDir, tname + "_" + v.name + "_body.png");
                        Shoot(tname, feat, face, body, log);
                        shots[v.name] = face;
                    }
                    string rawFace;
                    if (shots.TryGetValue("raw", out rawFace))
                        foreach (var v in variants) if (v.name != "raw" && shots.ContainsKey(v.name)) log.Add("  얼굴 샷 " + v.name + " vs raw: " + Compare(shots[v.name], rawFace));
                    foreach (var s in saved)
                    {
                        if (!gen.Contains(s.path)) continue;
                        var ti = AssetImporter.GetAtPath(s.path) as TextureImporter; if (ti == null) continue;
                        ti.textureCompression = s.comp; ti.crunchedCompression = s.crunch; ti.compressionQuality = s.quality; ti.SaveAndReimport();
                    }
                    log.Add("  임포트 설정 복원: " + gen.Count + "개");
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                foreach (var s in saved)
                {
                    var ti = AssetImporter.GetAtPath(s.path) as TextureImporter;
                    if (ti != null && (ti.textureCompression != s.comp || ti.crunchedCompression != s.crunch || ti.compressionQuality != s.quality))
                    { ti.textureCompression = s.comp; ti.crunchedCompression = s.crunch; ti.compressionQuality = s.quality; ti.SaveAndReimport(); log.Add("복원(finally): " + s.path); }
                }
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                File.WriteAllText(Path.Combine(outDir, "log_compress.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[Compress] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void Shoot(string prefabName, SobelOutlineFeature feat, string faceFile, string bodyFile, List<string> log)
        {
            NewStage(false);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(prefabName)));
            var trig = go.GetComponentInChildren<ToonFaceRig>(true); var prig = go.GetComponentInChildren<PainterlyRig>(true);
            var notes = new List<string>(); var head = HeadOf(go, Model(prefabName), notes); var body = RendererBounds(go);
            if (feat != null)
            {
                if (trig != null) { trig.ApplyOutline(); feat.settings.thickness = trig.outlineThickness * 2f; }
                else if (prig != null) { prig.ApplyInk(); feat.settings.thickness = prig.inkThickness * 2f; }
            }
            go.SetActive(false); go.SetActive(true); if (trig != null) trig.Apply();
            FBX2NPRCompare.SetSS(2);
            FBX2NPRCompare.Shoot(head, 700, 700, faceFile, Vector3.forward, false, 1.18f);
            FBX2NPRCompare.Shoot(body, 700, 900, bodyFile, Vector3.forward, false, 1.12f);
            FBX2NPRCompare.SetSS(1);
            foreach (var n in notes) log.Add("  " + n);
            UnityEngine.Object.DestroyImmediate(go);
        }

        static string Compare(string a, string b)
        {
            var ta = new Texture2D(2, 2); ta.LoadImage(File.ReadAllBytes(a)); var tb = new Texture2D(2, 2); tb.LoadImage(File.ReadAllBytes(b));
            if (ta.width != tb.width || ta.height != tb.height) return "크기 다름";
            var pa = ta.GetPixels32(); var pb = tb.GetPixels32(); long sum = 0; int over8 = 0, over24 = 0, max = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                int d = Math.Max(Math.Abs(pa[i].r - pb[i].r), Math.Max(Math.Abs(pa[i].g - pb[i].g), Math.Abs(pa[i].b - pb[i].b)));
                sum += Math.Abs(pa[i].r - pb[i].r) + Math.Abs(pa[i].g - pb[i].g) + Math.Abs(pa[i].b - pb[i].b);
                if (d > 8) over8++; if (d > 24) over24++; if (d > max) max = d;
            }
            UnityEngine.Object.DestroyImmediate(ta); UnityEngine.Object.DestroyImmediate(tb);
            return string.Format("평균 채널차 {0:F2}, 8 초과 {1:F2}%, 24 초과 {2:F3}%, 최대 {3}", sum / (3.0 * pa.Length), 100.0 * over8 / pa.Length, 100.0 * over24 / pa.Length, max);
        }

        /// <summary>검증 씬을 StandaloneWindows64 로 빌드한다 (Build/RuntimeCheck/FBX2NPRRuntimeCheck.exe). 환경변수 FBX2NPR_RC_FIXSHADER=1 이면 외곽선 피처에 셰이더 참조를 넣고 빌드한다.</summary>
        public static void Build()
        {
            string outDir = Path.Combine(OutRoot, "build"); Directory.CreateDirectory(outDir);
            var log = new List<string>();
            try
            {
                log.Add("활성 빌드 타깃: " + EditorUserBuildSettings.activeBuildTarget + ", 요청 타깃 StandaloneWindows64");
                // 셰이더 참조 저장은 씬을 만들기 전에 — 씬을 만들면 리그들이 피처 값을 바꾸는데, 그 값까지 렌더러 에셋에 저장되지 않게
                if (Environment.GetEnvironmentVariable("FBX2NPR_RC_FIXSHADER") == "1") log.Add("외곽선 셰이더 참조: " + EnsureOutlineShaderReference());
                else
                {
                    var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset"); SobelOutlineFeature f = null;
                    if (rd != null) foreach (var rf in rd.rendererFeatures) { f = rf as SobelOutlineFeature; if (f != null) break; }
                    log.Add("외곽선 피처 셰이더 참조(디스크 상태): " + (f == null ? "피처 없음" : f.shader != null ? f.shader.name : "비어 있음 (Shader.Find 에 의존)"));
                }
                string scenePath = BuildScene();
                // 환경변수 FBX2NPR_RC_BUILDDIR 이면 그 폴더에 (최종 데모 빌드 Build/RuntimeCheck 를 건드리지 않고 따로 빌드할 때)
                string buildDir = Environment.GetEnvironmentVariable("FBX2NPR_RC_BUILDDIR"); if (string.IsNullOrEmpty(buildDir)) buildDir = Path.Combine(ProjectRoot, "Build", "RuntimeCheck");
                Directory.CreateDirectory(buildDir);
                string exe = Path.Combine(buildDir, "FBX2NPRRuntimeCheck.exe");
                var opts = new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = exe, target = BuildTarget.StandaloneWindows64, options = BuildOptions.None };
                var rep = BuildPipeline.BuildPlayer(opts);
                var s = rep.summary;
                log.Add(string.Format("결과: {0}, {1:F0}초, 크기 {2:F1}MB, 오류 {3}, 경고 {4}, 출력 {5}", s.result, s.totalTime.TotalSeconds, s.totalSize / 1048576.0, s.totalErrors, s.totalWarnings, s.outputPath));
                foreach (var step in rep.steps)
                    foreach (var m in step.messages)
                    {
                        if (m.type == LogType.Error || m.type == LogType.Exception || m.type == LogType.Assert) log.Add("  [" + m.type + "] " + step.name + ": " + Trunc(m.content, 400));
                        else if (m.type == LogType.Warning && m.content.IndexOf("hader", StringComparison.Ordinal) >= 0) log.Add("  [경고] " + step.name + ": " + Trunc(m.content, 300));
                    }
                int nShader = 0;
                foreach (var pa in rep.packedAssets)
                    foreach (var c in pa.contents)
                    {
                        string sp = c.sourceAssetPath ?? "";
                        if (sp.EndsWith(".shader") || sp.EndsWith(".shadergraph")) { nShader++; if (sp.Contains("FBX2NPR") || sp.Contains("NormalTest")) log.Add(string.Format("  포함 셰이더: {0} ({1:F1}KB)", sp, c.packedSize / 1024.0)); }
                        else if (sp.StartsWith("Assets/FBX2NPRConverted") && sp.EndsWith(".png")) log.Add(string.Format("  포함 텍스처: {0} ({1:F2}MB)", sp, c.packedSize / 1048576.0));
                    }
                log.Add("  포함 셰이더 수(전체): " + nShader);
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(outDir, "build_log.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[Build] " + l);
            }
        }
        static string Trunc(string s, int n) { s = s.Replace("\n", " "); return s.Length <= n ? s : s.Substring(0, n) + "…"; }

        /// <summary>외곽선 피처에 셰이더 에셋 참조를 넣는다. 참조가 없는 셰이더는 빌드에서 빠져 Shader.Find 가 null 을 돌려준다.</summary>
        public static string EnsureOutlineShaderReference()
        {
            // 참조 자체는 FBX2NPRConverter.FindFeature 가 비어 있을 때 넣는다 (변환 도구도 같은 경로를 지나므로 새로 변환한 프로젝트는 처음부터 빌드 안전). 여기서는 저장만 보탠다.
            var feat = FBX2NPRConverter.FindFeature(); if (feat == null) return "외곽선 피처 없음";
            if (feat.shader == null) return "셰이더 에셋 없음: " + OutlineShaderPath;
            AssetDatabase.SaveAssets();
            return "참조 " + feat.shader.name + " (렌더러 에셋 저장)";
        }
    }

    /// <summary>배치 플레이 모드 검증이 끝나(드라이버가 ExitPlaymode) 편집 모드로 돌아오면 에디터를 끝낸다. Play() 가 세션 플래그를 켠다.</summary>
    [InitializeOnLoad]
    static class FBX2NPRRuntimeCheckExit
    {
        static FBX2NPRRuntimeCheckExit() { EditorApplication.playModeStateChanged += OnChange; }
        static void OnChange(PlayModeStateChange s)
        {
            if (s != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool("FBX2NPRRC.exitAfterPlay", false)) return;
            SessionState.EraseBool("FBX2NPRRC.exitAfterPlay");
            Debug.Log("[RuntimeCheck] 플레이 모드 종료 → 에디터 종료");
            EditorApplication.delayCall += () => EditorApplication.Exit(0);
        }
    }
}
