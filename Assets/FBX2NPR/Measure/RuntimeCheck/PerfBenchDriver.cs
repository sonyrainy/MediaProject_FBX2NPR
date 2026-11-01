using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.RuntimeCheck
{
    /// <summary>
    /// 성능 측정 드라이버. 플레이 모드(에디터)와 빌드된 플레이어 둘 다에서 같은 일을 한다.
    /// 같은 캐릭터를 counts 명(기본 1/6/30) 격자로 세우고 설정마다 프레임 시간을 잰다:
    ///   원본 재질(모델 그대로) / 변환한 툰 프리팹 외곽선 끔 / 외곽선 켬.
    /// 카메라는 가장 큰 격자에 맞춰 고정하고 width x height 렌더 텍스처에 그린다 (게임 뷰 크기와 무관하게 같은 화소 수).
    /// 설정마다 준비 warmup 프레임 뒤 frames 프레임의 unscaledDeltaTime 을 모아 평균·중앙값·95% 를 낸다. vSync 끔, 프레임 제한 없음.
    /// GPU 시간과 CPU 메인·렌더 스레드 시간은 FrameTimingManager 가 켜져 있을 때만 (빌드는 Player Settings 의 Frame Timing Stats). 셋을 같이 보면 병목이 어디인지 갈린다.
    /// 배치·SetPass·드로우 콜 수는 ProfilerRecorder 렌더 카운터를 읽을 수 있을 때만.
    /// 결과 perf.csv, perf_log.txt, 가장 많은 인원의 장면 PNG 는 -captureDir [경로], 없으면 outDir, 그것도 없으면 persistentDataPath/fbx2npr_perf.
    /// 명령줄: -counts 30 (인원 바꾸기, 쉼표로 여럿) / -experiments (원인 나누기 설정 2개 추가) / -profile (개발 빌드에서 설정마다 측정 뒤 profileFrames 프레임을 profile_[설정]_[인원].raw 로 기록).
    /// </summary>
    public class PerfBenchDriver : MonoBehaviour
    {
        public string outDir;
        public GameObject[] variants; public string[] variantNames; public Avatar[] avatars;
        public RuntimeAnimatorController controller;
        [Tooltip("변환한 프리팹 리그가 켜고 끄는 외곽선 피처 (씬에 하나). 끝나면 처음 상태로 되돌린다.")]
        public ScriptableRendererFeature outlineFeature;
        public Camera cam;
        public int[] counts = { 1, 6, 30 };
        public int columns = 6; public float spacingX = 1.0f, spacingZ = 1.3f;
        public int warmup = 120, frames = 600, profileFrames = 120;
        public int width = 1920, height = 1080;
        public bool quitWhenDone = true;

        class Config { public string name; public int variant; public bool outline, rigOff, clearBlocks; }
        string dir; readonly List<string> log = new List<string>(), csv = new List<string>();
        readonly FrameTiming[] ft = new FrameTiming[1];

        static string Arg(string key)
        {
            var a = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == key) return a[i + 1];
            return null;
        }
        static bool HasArg(string key) { return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), key) >= 0; }
        void L(string s) { log.Add(s); Debug.Log("[PerfBench] " + s); }
        static string F(float v) { return v.ToString("F2", CultureInfo.InvariantCulture); }

        IEnumerator Start()
        {
            dir = Arg("-captureDir") ?? (string.IsNullOrEmpty(outDir) ? Path.Combine(Application.persistentDataPath, "fbx2npr_perf") : outDir);
            Directory.CreateDirectory(dir);
            Application.runInBackground = true; Application.targetFrameRate = -1; QualitySettings.vSyncCount = 0;
            var rt = new RenderTexture(width, height, 24) { name = "PerfBench" }; cam.targetTexture = rt;
            bool featWas = outlineFeature != null && outlineFeature.isActive;
            string countsArg = Arg("-counts"); if (!string.IsNullOrEmpty(countsArg)) counts = System.Array.ConvertAll(countsArg.Split(','), int.Parse);
            bool profile = HasArg("-profile");
            if (profile && !Profiler.supported) { L("-profile 은 개발 빌드에서만 된다. 프로파일 기록 없이 진행"); profile = false; }
            L(string.Format("환경: {0}, GPU {1} ({2}), CPU {3}, Unity {4}, 렌더 {5}x{6}, 준비 {7}프레임 + 측정 {8}프레임",
                Application.isEditor ? "에디터 플레이 모드" : "빌드 플레이어", SystemInfo.graphicsDeviceName, SystemInfo.graphicsDeviceType, SystemInfo.processorType,
                Application.unityVersion, width, height, warmup, frames));
            for (int v = 0; v < variants.Length; v++) L(string.Format("변형 {0}: {1}, 삼각형 {2}개", v, variantNames[v], Triangles(variants[v])));

            var configs = new List<Config>
            {
                new Config { name = "원본 재질", variant = 0, outline = false },
                new Config { name = "툰 외곽선 끔", variant = 1, outline = false },
                new Config { name = "툰 외곽선 켬", variant = 1, outline = true },
            };
            if (HasArg("-experiments"))
            {
                // 원인 나누기: 리그의 매 프레임 갱신을 끄면(블록에는 첫 프레임 값이 남는다) 스크립트 비용이, 블록까지 지우면 프로퍼티 블록 비용이 빠진다
                configs.Add(new Config { name = "툰 리그 갱신 끔", variant = 1, rigOff = true });
                configs.Add(new Config { name = "툰 블록 없음", variant = 1, rigOff = true, clearBlocks = true });
            }
            int maxN = 0; foreach (int n in counts) maxN = Mathf.Max(maxN, n);
            var recBatches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            var recSetPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            var recDraws = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            csv.Add("config,count,frames,mean_ms,p50_ms,p95_ms,fps,gpu_mean_ms,cpu_main_ms,cpu_render_ms,batches,setpass,drawcalls");
            for (int ci = 0; ci < configs.Count; ci++)
                foreach (int n in counts)
                {
                    var c = configs[ci];
                    var spawned = Spawn(c.variant, n);
                    yield return null;   // 리그 OnEnable 이 피처를 켠 뒤에 정한다 (리그는 OnEnable 에서만 피처를 건드린다)
                    if (outlineFeature != null) outlineFeature.SetActive(c.outline);
                    if (c.rigOff) foreach (var go in spawned) foreach (var rig in go.GetComponentsInChildren<ToonFaceRig>(true)) rig.enabled = false;
                    if (c.clearBlocks)
                        foreach (var go in spawned)
                            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) { r.SetPropertyBlock(null); for (int m = 0; m < r.sharedMaterials.Length; m++) r.SetPropertyBlock(null, m); }
                    for (int i = 0; i < warmup; i++) yield return null;
                    var dt = new List<float>(frames); float gpuSum = 0f, mainSum = 0f, renderSum = 0f; int gpuN = 0, cpuN = 0;
                    double batchSum = 0, setPassSum = 0, drawSum = 0; int recN = 0;
                    for (int i = 0; i < frames; i++)
                    {
                        yield return null;
                        dt.Add(Time.unscaledDeltaTime * 1000f);
                        FrameTimingManager.CaptureFrameTimings();
                        if (FrameTimingManager.GetLatestTimings(1, ft) > 0)
                        {
                            if (ft[0].gpuFrameTime > 0) { gpuSum += (float)ft[0].gpuFrameTime; gpuN++; }
                            if (ft[0].cpuMainThreadFrameTime > 0) { mainSum += (float)ft[0].cpuMainThreadFrameTime; renderSum += (float)ft[0].cpuRenderThreadFrameTime; cpuN++; }
                        }
                        if (recBatches.Valid && recBatches.LastValue > 0) { batchSum += recBatches.LastValue; setPassSum += recSetPass.LastValue; drawSum += recDraws.LastValue; recN++; }
                    }
                    dt.Sort(); float mean = 0f; foreach (var d in dt) mean += d; mean /= dt.Count;
                    float p50 = dt[dt.Count / 2], p95 = dt[Mathf.Min(dt.Count - 1, (int)(dt.Count * 0.95f))];
                    string gpu = gpuN > 0 ? F(gpuSum / gpuN) : "", main = cpuN > 0 ? F(mainSum / cpuN) : "", render = cpuN > 0 ? F(renderSum / cpuN) : "";
                    string batches = recN > 0 ? F((float)(batchSum / recN)) : "", setPass = recN > 0 ? F((float)(setPassSum / recN)) : "", draws = recN > 0 ? F((float)(drawSum / recN)) : "";
                    csv.Add(string.Join(",", new[] { c.name, n.ToString(), dt.Count.ToString(), F(mean), F(p50), F(p95), F(1000f / mean), gpu, main, render, batches, setPass, draws }));
                    L(string.Format("{0}, {1}명: 평균 {2} ms (중앙값 {3}, 95% {4}), {5} fps{6}{7}{8}", c.name, n, F(mean), F(p50), F(p95), F(1000f / mean),
                        gpuN > 0 ? ", GPU " + gpu + " ms" : "", cpuN > 0 ? ", CPU 메인 " + main + " ms / 렌더 " + render + " ms" : "",
                        recN > 0 ? ", 배치 " + batches + " / SetPass " + setPass + " / 드로우 " + draws : ""));
                    if (profile)
                    {
                        // 측정이 끝난 뒤에 따로 기록한다 (프로파일러 부하가 위 프레임 시간에 섞이지 않게)
                        string raw = Path.Combine(dir, "profile_" + ci + "_" + n + ".raw");
                        Profiler.logFile = raw; Profiler.enableBinaryLog = true; Profiler.enabled = true;
                        for (int i = 0; i < profileFrames; i++) yield return null;
                        Profiler.enabled = false; Profiler.enableBinaryLog = false; Profiler.logFile = "";
                        L("프로파일 기록: " + c.name + ", " + n + "명 -> " + raw);
                    }
                    if (n == maxN) Save(rt, Path.Combine(dir, "perf_scene_" + ci + ".png"));   // 설정 순서 번호 (0 원본, 1 툰 외곽선 끔, 2 켬, 3·4 실험)
                    foreach (var go in spawned) Destroy(go);
                    yield return null; yield return null;
                }
            recBatches.Dispose(); recSetPass.Dispose(); recDraws.Dispose();
            if (outlineFeature != null) outlineFeature.SetActive(featWas);
            File.WriteAllText(Path.Combine(dir, "perf.csv"), string.Join("\n", csv.ToArray()), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "perf_log.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
            cam.targetTexture = null; rt.Release();
            if (quitWhenDone)
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
        }

        /// <summary>n 명을 가로 최대 columns 명씩, 앞줄부터 뒤로 세운다 (줄마다 가운데 정렬). 애니메이터는 같은 컨트롤러로 제자리 재생.</summary>
        List<GameObject> Spawn(int variant, int n)
        {
            var list = new List<GameObject>();
            int cols = Mathf.Min(columns, n);
            for (int i = 0; i < n; i++)
            {
                int row = i / cols, col = i % cols, inRow = Mathf.Min(cols, n - row * cols);
                var go = Instantiate(variants[variant], new Vector3((col - (inRow - 1) * 0.5f) * spacingX, 0f, -row * spacingZ), Quaternion.identity);
                var an = go.GetComponent<Animator>(); if (an == null) an = go.AddComponent<Animator>();
                if (an.avatar == null && avatars != null && variant < avatars.Length) an.avatar = avatars[variant];
                an.runtimeAnimatorController = controller; an.applyRootMotion = false; an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                list.Add(go);
            }
            return list;
        }

        static int Triangles(GameObject go)
        {
            int t = 0;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = null; var smr = r as SkinnedMeshRenderer;
                if (smr != null) m = smr.sharedMesh; else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
                if (m != null) t += m.triangles.Length / 3;
            }
            return t;
        }

        static void Save(RenderTexture rt, string path)
        {
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG()); Destroy(tex);
        }
    }
}
