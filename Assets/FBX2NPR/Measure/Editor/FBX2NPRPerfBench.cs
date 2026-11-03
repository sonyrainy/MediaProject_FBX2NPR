// FBX2NPRPerfBench.cs — 성능 측정. 같은 캐릭터를 1/6/30명 세우고 원본 재질 / 툰 외곽선 끔 / 툰 외곽선 켬의 프레임 시간을 잰다 (플레이 모드·빌드 공용 드라이버 PerfBenchDriver).
// 플레이 모드: FBX2NPR.EditorTools.FBX2NPRPerfBench.Play (결과 NormalTestOut/runtime/perf/editor) / 빌드: ...Build (Build/PerfBench/FBX2NPRPerfBench.exe, 실행할 때 -captureDir 로 결과 폴더 지정)
// 원인 나누기: 실행할 때 -counts 30 -experiments, 프로파일러 기록은 ...BuildDevelopment (Build/PerfBenchDev) 를 -profile 과 함께 실행
// 환경변수 FBX2NPR_PERF_MODEL 로 캐릭터를 바꾼다 (기본 Ch07, TestSet 의 모델과 변환한 <모델>_Toon 프리팹이 있어야 한다).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using FBX2NPR.RuntimeCheck;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRPerfBench
    {
        public const string ScenePath = "Assets/FBX2NPRRuntimeCheck/PerfBench.unity";
        static string ProjectRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }
        public static string OutDir { get { return Path.Combine(FBX2NPRRuntimeCheck.OutRoot, "perf"); } }

        [MenuItem("FBX2NPR/게임 적용 검증/성능 측정 씬 만들기")]
        public static string BuildScene()
        {
            string model = Environment.GetEnvironmentVariable("FBX2NPR_PERF_MODEL"); if (string.IsNullOrEmpty(model)) model = "Ch07";
            var notes = new List<string>();
            FBX2NPRRuntimeCheck.NewStage(true);
            var scene = SceneManager.GetActiveScene();
            string modelPath = FBX2NPRHumanoidSetup.ModelPath(model), toonPath = FBX2NPRRuntimeCheck.PrefabPath(model + "_Toon");
            var orig = modelPath != null ? AssetDatabase.LoadAssetAtPath<GameObject>(modelPath) : null;
            var toon = AssetDatabase.LoadAssetAtPath<GameObject>(toonPath);
            if (orig == null || toon == null) throw new Exception("모델 또는 툰 프리팹 없음: " + modelPath + " / " + toonPath);
            var avatar = FBX2NPRHumanoidSetup.AvatarOf(modelPath);
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(FBX2NPRHumanoidSetup.ControllerPath);
            var rig = toon.GetComponentInChildren<ToonFaceRig>(true);

            var drv = new GameObject("PerfBenchDriver").AddComponent<PerfBenchDriver>();
            // 카메라: 가장 큰 격자(가로 columns 명, 앞줄 z=0 부터 뒤로)를 앞에서 내려다보게 고정. 세로 FOV 30, 16:9
            int maxN = 0; foreach (int n in drv.counts) maxN = Mathf.Max(maxN, n);
            int cols = Mathf.Min(drv.columns, maxN), rows = Mathf.CeilToInt(maxN / (float)cols);
            float gridW = (cols - 1) * drv.spacingX + 0.8f, gridD = (rows - 1) * drv.spacingZ;
            float aspect = drv.width / (float)drv.height, fovX = 2f * Mathf.Atan(Mathf.Tan(15f * Mathf.Deg2Rad) * aspect);
            float dist = (gridW * 0.5f + 0.4f) / Mathf.Tan(fovX * 0.5f);
            var camGo = new GameObject("PerfCam"); var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.20f, 0.20f, 0.22f);
            cam.fieldOfView = 30f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 60f; cam.allowHDR = false; cam.allowMSAA = false;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None; acd.dithering = false; acd.renderShadows = true;
            camGo.transform.position = new Vector3(0f, 3.0f, dist); camGo.transform.LookAt(new Vector3(0f, 0.85f, -gridD * 0.5f));
            camGo.tag = "MainCamera";

            drv.outDir = Path.Combine(OutDir, "editor");
            drv.variants = new[] { orig, toon }; drv.variantNames = new[] { model + " 원본", model + "_Toon" }; drv.avatars = new[] { avatar, avatar };
            drv.controller = ctrl; drv.cam = cam;
            drv.outlineFeature = rig != null && rig.outlineFeature != null ? rig.outlineFeature : FBX2NPRConverter.FindFeature();
            notes.Add(string.Format("모델 {0}, 툰 프리팹 {1}, 아바타 {2}, 컨트롤러 {3}, 외곽선 피처 {4}", modelPath, toonPath, avatar != null ? avatar.name : "없음", ctrl != null ? ctrl.name : "없음", drv.outlineFeature != null ? drv.outlineFeature.name : "없음"));
            notes.Add(string.Format("격자 최대 {0}명 ({1}x{2}), 카메라 거리 {3:F2}m", maxN, cols, rows, dist));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, "scene_notes.txt"), string.Join("\n", notes.ToArray()), new UTF8Encoding(true));
            foreach (var n in notes) Debug.Log("[PerfBench] " + n);
            return ScenePath;
        }

        [MenuItem("FBX2NPR/게임 적용 검증/성능 측정 (플레이 모드)")]
        public static void Play()
        {
            BuildScene();
            EditorApplication.EnterPlaymode();
        }

        /// <summary>성능 측정 씬을 StandaloneWindows64 로 빌드한다 (Build/PerfBench/FBX2NPRPerfBench.exe). 에디터 오버헤드 없이 재려면 이 빌드를 -captureDir 와 함께 실행한다.</summary>
        public static void Build() { Build(false); }

        /// <summary>개발 빌드 (Build/PerfBenchDev). 프레임 시간은 릴리스보다 느리니 비교에 쓰지 않고, -profile 로 프로파일러 기록(.raw)을 남겨 어디에 시간이 드는지 볼 때 쓴다.</summary>
        public static void BuildDevelopment() { Build(true); }

        static void Build(bool development)
        {
            var log = new List<string>(); Directory.CreateDirectory(OutDir);
            bool timingWas = PlayerSettings.enableFrameTimingStats;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;   // 빌드에서도 GPU·CPU 스레드 시간을 받는다. 끝나면 되돌린다
                string scenePath = BuildScene();
                string buildDir = Path.Combine(ProjectRoot, "Build", development ? "PerfBenchDev" : "PerfBench"); Directory.CreateDirectory(buildDir);
                var opts = new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = Path.Combine(buildDir, "FBX2NPRPerfBench.exe"), target = BuildTarget.StandaloneWindows64, options = development ? BuildOptions.Development : BuildOptions.None };
                var s = BuildPipeline.BuildPlayer(opts).summary;
                log.Add(string.Format("결과: {0}, {1:F0}초, 크기 {2:F1}MB, 오류 {3}, 경고 {4}, 출력 {5}", s.result, s.totalTime.TotalSeconds, s.totalSize / 1048576.0, s.totalErrors, s.totalWarnings, s.outputPath));
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                PlayerSettings.enableFrameTimingStats = timingWas;
                // 빌드 전처리(lilToon 최적화)가 도중에 프로젝트 설정을 저장해 켠 값이 디스크에 남으므로, 되돌린 값을 다시 저장한다.
                // lilToon 은 빌드 전에 셰이더 기능을 끄는데 이 프로젝트에서는 빌드 뒤 복원이 돌지 않았다 (2026-11-01). 공식 메뉴로 다시 켠다 (lilToon 비교 측정이 그림자를 잃지 않게)
                EditorApplication.ExecuteMenuItem("Assets/lilToon/[Shader] Refresh shaders");
                AssetDatabase.SaveAssets();
                File.WriteAllText(Path.Combine(OutDir, development ? "build_dev_log.txt" : "build_log.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[PerfBench] " + l);
            }
        }
    }
}
