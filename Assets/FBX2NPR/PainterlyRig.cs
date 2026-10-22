using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR
{
    /// <summary>
    /// 회화풍 모드 프리팹의 손잡이. 재질은 FBX2NPR/PainterlyLit(= URP Lit, 외곽선용 노멀 패스만 다름. 붓질 알베도, 노멀맵 없음, 낮은 광택)이고,
    /// (1) 화면공간 외곽선을 '실루엣만 잡는 얇은 잉크 라인'으로 쓰는 슬라이더와 (2) 눈매 잉크(원본의 아이라인·속눈썹을 붓질 피부 위에 Darken 으로 얹는 세기) 슬라이더가 있다.
    /// 잉크 라인은 화면 전체에 걸리는 렌더러 피처라 재질 옵션이 아니라 여기(인스펙터 슬라이더)에 둔다. 0 이면 끈다.
    /// 눈매 잉크는 변환 도구가 붓질 텍스처의 알파에 구워 둔 비중을 런타임에 섞는다: InkBlend 셰이더로 임시 RenderTexture 에 블릿한 뒤 GPU 에서 Texture2D 로 복사해
    /// 렌더러 프로퍼티 블록의 _BaseMap 으로 끼운다. RenderTexture 를 그대로 끼우면 에디터 플레이 모드에서 몇 프레임 뒤 내용이 검게 비워졌다 (기록 97번) — RenderTexture 는 내용 유지를 보장하지 않는다.
    /// 툰 모드 리그(ToonFaceRig)와 같은 외곽선 피처를 공유하므로, 한 씬에 두 모드 프리팹이 같이 있으면 yieldToToon 규칙으로 툰이 우선한다 (기록 90번).
    /// 그때 외곽선은 노멀 엣지(안쪽 선)를 쓰므로, 화면에서 작아진 회화풍 캐릭터는 innerLineFade 로 외곽선용 노멀을 카메라 방향으로 눕혀 안쪽 선을 지운다 (기록 98번).
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10000)]
    public class PainterlyRig : MonoBehaviour
    {
        [Header("잉크 외곽선 (화면공간 실루엣 선)")]
        [Tooltip("실루엣 잉크 라인 세기. 0 = 없음, 1 = 기본(진하기 0.45). 노멀 엣지(얼굴 안쪽 선)는 쓰지 않는다.")]
        [Range(0f, 1f)] public float inkLine = 1f;
        [Tooltip("선 두께 (픽셀).")]
        [Range(0.3f, 2f)] public float inkThickness = 0.6f;
        [Tooltip("inkLine 이 1 일 때의 진하기.")]
        [Range(0f, 1f)] public float inkDarkness = 0.45f;
        [Range(0f, 40f)] public float inkDepthScale = 12f;
        [Range(0f, 1f)] public float inkThreshold = 0.30f;
        [Tooltip("잉크를 그리는 화면공간 외곽선 피처. 변환 도구가 PC_Renderer 의 피처를 넣어 둔다.")]
        public ScriptableRendererFeature outlineFeature;
        [Tooltip("켜면 같은 씬에 외곽선을 구동하는 툰 리그(ToonFaceRig, driveOutlineSettings)가 있을 때 잉크 값을 넣지 않는다. 외곽선 피처는 화면 전체에 하나뿐이라 두 모드가 같이 있으면 한쪽이 양보해야 하는데, 툰 쪽은 안쪽 선(노멀 엣지)이 없으면 얼굴이 달라지고 회화풍 쪽은 툰 값으로도 큰 차이가 없다 (기록 90번). 끄면 나중에 켜진 쪽이 이긴다.")]
        public bool yieldToToon = true;

        [Header("멀어지면 안쪽 선 지우기 (툰 리그와 한 씬일 때)")]
        [Tooltip("x = 지우기 시작, y = 다 지움 (픽셀 하나가 덮는 실제 크기, 미터). 변환 도구가 머리 높이로 채운다 — 머리가 화면에서 380픽셀보다 작아지면 시작해 280픽셀에서 다 지움(툰과 같은 기준). " +
                 "툰과 한 씬이면 외곽선 피처가 툰 값(노멀 엣지)으로 도는데, 회화풍 재질에는 툰 셰이더의 이 장치가 없어 넓은 화면에서 얼굴이 갈색 덩어리로 뭉쳤다 (기록 97·98번). " +
                 "외곽선이 노멀 엣지를 쓸 때만 넣는다 — 회화풍 혼자인 씬(노멀 엣지 0)에서는 아무것도 바뀌지 않는다. 0 이면 끔.")]
        public Vector2 innerLineFade = Vector2.zero;

        [Header("눈매 잉크 (원본의 아이라인·속눈썹을 붓질 피부 위에 Darken 으로)")]
        [Tooltip("0 = 없음(붓질 결과 그대로), 1 = 원본의 어두운 선을 다 얹음. 변환 도구가 텍스처 알파에 구워 둔 비중을 런타임에 섞으므로 슬라이더를 움직이면 바로 반영된다.")]
        [Range(0f, 1f)] public float eyeInk = 1f;
        [Serializable] public class InkEntry { public Material material; public Texture2D painted; public Texture2D original; }
        [Tooltip("눈매 잉크를 섞을 재질과 텍스처. 변환 도구가 채운다. 비어 있으면 잉크가 텍스처에 구워진 옛 프리팹이라 슬라이더가 듣지 않는다 (다시 변환하면 된다).")]
        public List<InkEntry> inkEntries = new List<InkEntry>();
        [Tooltip("섞기 셰이더 (Hidden/FBX2NPR/InkBlend). 참조로 들고 있어야 빌드에 들어간다.")]
        public Shader inkBlendShader;

        public string sourceModelGuid = "";
        [HideInInspector] public List<string> generatedAssets = new List<string>();

        Material blendMat; Texture2D[] inkTex; MaterialPropertyBlock block; string lastPath = "";
        Vector4 fadeNow; bool fadeSet;   // 지금 렌더러에 넣은 _InnerLineFade (0 이면 안 넣음)
        public const string FadeShaderName = "FBX2NPR/PainterlyLit";

        void OnEnable() { ApplyInk(); ApplyEyeInk(); ApplyLineFade(true); }
        void OnDisable() { ReleaseEyeInk(); }
        void OnValidate() { if (!gameObject.scene.IsValid()) return; ApplyInk(); ApplyEyeInk(); ApplyLineFade(true); }   // 프리팹 에셋 자체(씬 밖)에서는 넣을 대상이 없다
        // 외곽선 값은 다른 리그(툰)가 언제든 바꿀 수 있으므로 매 프레임 확인만 하고, 바뀌었을 때만 렌더러에 넣는다
        void LateUpdate() { ApplyLineFade(false); }

        /// <summary>외곽선 피처가 지금 노멀 엣지(안쪽 선)를 그리는가. 그때만 회화풍 재질의 '멀어지면 안쪽 선 지우기'가 필요하다.</summary>
        public bool WantsLineFade()
        {
            if (innerLineFade.y <= 0f) return false;
            var sf = outlineFeature as SobelOutlineFeature;
            return sf != null && sf.isActive && sf.settings != null && sf.settings.normalScale > 0f;
        }

        /// <summary>'멀어지면 안쪽 선 지우기' 값을 렌더러 프로퍼티 블록에 넣거나 뺀다. force 가 아니면 바뀌었을 때만.</summary>
        public void ApplyLineFade(bool force)
        {
            var want = WantsLineFade() ? new Vector4(innerLineFade.x, innerLineFade.y, 0f, 0f) : Vector4.zero;
            if (!force && fadeSet && want == fadeNow) return;
            fadeNow = want; fadeSet = true;
            RefreshBlocks();
        }

        /// <summary>진단: 안쪽 선 지우기 상태와 값을 넣은 재질 칸 수.</summary>
        public string DebugFade()
        {
            int slots = 0, withFade = 0; if (block == null) block = new MaterialPropertyBlock();
            var root = transform.parent != null ? transform.parent : transform;
            foreach (var rr in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = rr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || mats[i].shader == null || mats[i].shader.name != FadeShaderName) continue;
                    slots++; rr.GetPropertyBlock(block, i); if (block.GetVector("_InnerLineFade").y > 0f) withFade++;
                }
            }
            return string.Format("값 ({0:F5}, {1:F5})m/px, 외곽선 노멀 엣지 사용 {2} → {3}, {4} 셰이더 칸 {5}개 중 값 넣은 칸 {6}",
                innerLineFade.x, innerLineFade.y, WantsLineFade(), fadeNow.y > 0f ? "켬" : "끔", FadeShaderName, slots, withFade);
        }

        /// <summary>
        /// 이 리그가 관리하는 렌더러 프로퍼티 블록(눈매 잉크의 _BaseMap, 안쪽 선 지우기의 _InnerLineFade)을 재질 칸마다 다시 쓴다.
        /// 둘 다 없는 칸은 블록을 걷는다 — 블록이 있으면 그 렌더러는 SRP 배처를 못 타므로 필요할 때만 둔다.
        /// </summary>
        void RefreshBlocks()
        {
            if (block == null) block = new MaterialPropertyBlock();
            bool fade = fadeNow.y > 0f;
            var root = transform.parent != null ? transform.parent : transform;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    int e = IndexOfMaterial(mats[i]);
                    var ink = e >= 0 && inkTex != null && e < inkTex.Length ? inkTex[e] : null;
                    bool fadeSlot = fade && mats[i] != null && mats[i].shader != null && mats[i].shader.name == FadeShaderName;
                    if (ink == null && !fadeSlot)
                    {
                        if (e >= 0 || (mats[i] != null && mats[i].shader != null && mats[i].shader.name == FadeShaderName)) r.SetPropertyBlock(null, i);
                        continue;
                    }
                    block.Clear();
                    if (ink != null) block.SetTexture("_BaseMap", ink);
                    if (fadeSlot) block.SetVector("_InnerLineFade", fadeNow);
                    r.SetPropertyBlock(block, i);
                }
            }
        }

        public void ApplyInk()
        {
            var sf = outlineFeature as SobelOutlineFeature;
            if (sf == null) return;
            if (yieldToToon && ToonRigDrivesOutline()) return;
            if (inkLine <= 0f) { if (sf.isActive) sf.SetActive(false); return; }
            if (!sf.isActive) sf.SetActive(true);
            var st = sf.settings;
            st.darkness = inkDarkness * inkLine; st.thickness = inkThickness; st.depthScale = inkDepthScale; st.normalScale = 0f; st.threshold = inkThreshold;
        }

        /// <summary>같은 씬에 외곽선을 구동하는 툰 리그가 켜져 있는가. OnEnable·OnValidate 때만 부르므로 전체 검색이어도 된다.</summary>
        public static bool ToonRigDrivesOutline()
        {
            foreach (var r in FindObjectsByType<ToonFaceRig>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (r.driveOutlineSettings && r.outline && r.outlineFeature != null && r.isActiveAndEnabled) return true;
            return false;
        }

        public bool HasEyeInk { get { return inkEntries != null && inkEntries.Count > 0; } }

        /// <summary>진단: 눈매 잉크 텍스처(평균 색은 임시 RT 로 읽어서)와 렌더러에 끼운 _BaseMap 상태.</summary>
        public string DebugInk()
        {
            if (!HasEyeInk) return "잉크 없음";
            var sb = new System.Text.StringBuilder();
            sb.Append("경로 " + lastPath + ", blendMat " + (blendMat != null ? "있음" : "없음") + ", 텍스처 " + (inkTex == null ? "null" : inkTex.Length.ToString()));
            if (inkTex != null)
                for (int e = 0; e < inkTex.Length; e++)
                {
                    var tx = inkTex[e]; if (tx == null) { sb.Append(" | tex" + e + " null"); continue; }
                    sb.Append(string.Format(" | tex{0} {1}x{2} 밉 {3}", e, tx.width, tx.height, tx.mipmapCount));
                    // 원해상도(밉 0)와 1/32 크기(하위 밉을 샘플)로 각각 평균 — 하위 밉이 비면 둘째 값이 검다
                    foreach (int div in new[] { 1, 32 })
                    {
                        int ww = Mathf.Max(1, tx.width / div), hh = Mathf.Max(1, tx.height / div);
                        var rt = RenderTexture.GetTemporary(ww, hh, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        Graphics.Blit(tx, rt);
                        var prev = RenderTexture.active; RenderTexture.active = rt;
                        var t = new Texture2D(ww, hh, TextureFormat.RGBA32, false, false); t.ReadPixels(new Rect(0, 0, ww, hh), 0, 0); t.Apply(); RenderTexture.active = prev;
                        RenderTexture.ReleaseTemporary(rt);
                        var px = t.GetPixels32(); int step = Mathf.Max(1, px.Length / 4000); long r = 0, g = 0, b = 0; int n = 0; for (int i = 0; i < px.Length; i += step) { r += px[i].r; g += px[i].g; b += px[i].b; n++; }
                        DestroyNow(t);
                        sb.Append(string.Format(" 1/{0} 평균 ({1},{2},{3})", div, r / n, g / n, b / n));
                    }
                }
            if (block == null) block = new MaterialPropertyBlock();
            var root = transform.parent != null ? transform.parent : transform; int slots = 0, withTex = 0;
            foreach (var rr in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = rr.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) { int e = IndexOfMaterial(mats[i]); if (e < 0) continue; slots++; rr.GetPropertyBlock(block, i); var tx = block.GetTexture("_BaseMap"); if (tx != null && inkTex != null && tx == inkTex[e]) withTex++; }
            }
            sb.Append(" | 잉크 슬롯 " + slots + " 중 섞은 텍스처 연결 " + withTex);
            return sb.ToString();
        }

        /// <summary>눈매 잉크를 현재 eyeInk 로 다시 섞어 렌더러에 넣는다. 텍스처 하나당 블릿·복사 한 번이라 슬라이더를 끌어도 가볍다.</summary>
        public void ApplyEyeInk()
        {
            if (!HasEyeInk) return;
            var sh = inkBlendShader != null ? inkBlendShader : Shader.Find("Hidden/FBX2NPR/InkBlend");
            if (sh == null) return;
            if (blendMat == null) { blendMat = new Material(sh); blendMat.hideFlags = HideFlags.HideAndDontSave; }
            if (block == null) block = new MaterialPropertyBlock();
            if (inkTex == null || inkTex.Length != inkEntries.Count) { ReleaseTex(); inkTex = new Texture2D[inkEntries.Count]; }
            bool gpuCopy = (SystemInfo.copyTextureSupport & CopyTextureSupport.RTToTexture) != 0;
            for (int e = 0; e < inkEntries.Count; e++)
            {
                var en = inkEntries[e]; if (en == null || en.material == null || en.painted == null || en.original == null) continue;
                // 같은 텍스처를 쓰는 재질끼리는 섞은 텍스처 한 장을 같이 쓴다 (The Boss: 재질 4개가 텍스처 1장)
                int same = -1; for (int k = 0; k < e; k++) { var ek = inkEntries[k]; if (ek != null && ek.painted == en.painted && ek.original == en.original && inkTex[k] != null) { same = k; break; } }
                if (same >= 0) { if (inkTex[e] != null && inkTex[e] != inkTex[same]) DestroyNow(inkTex[e]); inkTex[e] = inkTex[same]; continue; }
                int w = en.painted.width, h = en.painted.height;
                // 밉맵은 자동 생성(autoGenerateMips)에 맡기지 않고 블릿 직후 명시적으로 만든다 — 자동 생성은 RT 가 다음에 쓰일 때 일어나서,
                // 바로 GPU 복사하면 플레이어(D3D12)에서 하위 밉이 빈 채로 복사돼 멀리서 검게, 가까이서 눈·목이 얼룩지게 나왔다 (기록 97번)
                var desc = new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0) { sRGB = true, useMipMap = true, autoGenerateMips = false, msaaSamples = 1 };
                var rt = RenderTexture.GetTemporary(desc);
                blendMat.SetTexture("_Orig", en.original); blendMat.SetFloat("_EyeInk", eyeInk);
                Graphics.Blit(en.painted, rt, blendMat);
                rt.GenerateMips();
                var tx = inkTex[e];
                bool useGpu = gpuCopy;
                bool sharedElsewhere = false; if (tx != null) for (int k = 0; k < inkTex.Length; k++) if (k != e && inkTex[k] == tx) { sharedElsewhere = true; break; }
                if (sharedElsewhere) tx = null;   // 예전에 공유하던 텍스처는 건드리지 않고 새로 만든다
                if (tx == null || tx.width != w || tx.height != h || tx.mipmapCount != rt.mipmapCount)
                {
                    if (tx != null) DestroyNow(tx);
                    tx = new Texture2D(w, h, TextureFormat.RGBA32, true, false);
                    tx.name = en.painted.name + "_ink"; tx.hideFlags = HideFlags.HideAndDontSave;
                    tx.wrapMode = en.painted.wrapMode; tx.filterMode = en.painted.filterMode; tx.anisoLevel = en.painted.anisoLevel;
                    if (useGpu && tx.mipmapCount == rt.mipmapCount) tx.Apply(false, true);   // CPU 사본을 버린다 (GPU 복사만 쓰므로)
                    else useGpu = false;
                    inkTex[e] = tx;
                }
                if (useGpu && !tx.isReadable) { Graphics.CopyTexture(rt, tx); lastPath = "GPU 복사"; }
                else
                {
                    // GPU 복사를 못 하면 읽어서 올린다 (느리지만 한 번뿐)
                    if (!tx.isReadable) { DestroyNow(tx); tx = new Texture2D(w, h, TextureFormat.RGBA32, true, false); tx.name = en.painted.name + "_ink"; tx.hideFlags = HideFlags.HideAndDontSave; tx.wrapMode = en.painted.wrapMode; tx.filterMode = en.painted.filterMode; tx.anisoLevel = en.painted.anisoLevel; inkTex[e] = tx; }
                    var prev = RenderTexture.active; RenderTexture.active = rt; tx.ReadPixels(new Rect(0, 0, w, h), 0, 0); tx.Apply(true, false); RenderTexture.active = prev;
                    lastPath = "읽어서 올림";
                }
                RenderTexture.ReleaseTemporary(rt);
            }
            RefreshBlocks();   // 섞은 텍스처를 _BaseMap 으로 (안쪽 선 지우기 값이 있으면 같은 블록에 같이)
        }

        int IndexOfMaterial(Material m)
        {
            if (m == null || inkEntries == null) return -1;
            for (int e = 0; e < inkEntries.Count; e++) if (inkEntries[e] != null && inkEntries[e].material == m) return e;
            return -1;
        }

        /// <summary>프로퍼티 블록(눈매 잉크·안쪽 선 지우기)을 걷고 섞은 텍스처를 놓는다. 렌더러는 재질의 붓질 텍스처(잉크 없음)로 돌아간다.</summary>
        public void ReleaseEyeInk()
        {
            ReleaseTex();
            fadeNow = Vector4.zero; fadeSet = false;   // 켜져 있으면 다음 LateUpdate 가 다시 넣는다
            RefreshBlocks();
            if (blendMat != null) { DestroyNow(blendMat); blendMat = null; }
        }
        void ReleaseTex() { if (inkTex == null) return; var done = new HashSet<Texture2D>(); foreach (var t in inkTex) if (t != null && done.Add(t)) DestroyNow(t); inkTex = null; }
        static void DestroyNow(UnityEngine.Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
    }
}
