using System.Collections.Generic;
using UnityEngine;

namespace FBX2NPR
{
    /// <summary>
    /// 얼굴 명암선을 씬에서 직접 조절하는 컴포넌트 (계획서 목표 11: 3D 도구를 켜지 않고 유니티 화면에서 조절).
    ///
    /// 인스펙터 슬라이더를 움직이거나, 씬의 표식(빛·코끝·눈 A·눈 B)을 이동·회전하면 얼굴 재질에 바로 반영된다.
    /// 코끝·눈 표식의 처음 위치는 미리보기 생성기가 자동으로 찾은 값이다(FaceLandmarks).
    /// 자동으로 못 찾은 캐릭터(Jones 처럼 눈 메시가 없는 경우)는 표식을 제자리로 옮기고 '눈 피하기'를 켜면 된다.
    ///
    /// 셰이더는 씬 라이트가 아니라 _LightDir 를 쓴다. '빛' 표식이 바라보는 방향으로 빛이 나아간다 (디렉셔널 라이트와 같은 규칙).
    ///
    /// 변환한 프리팹에서는 이 오브젝트의 스케일을 직접 바꾸지 말 것. 리그의 월드 스케일을 프리팹 배율로 읽어 코·눈 영역 크기에 곱한다.
    /// 플레이 중에는 다른 스크립트(시선 추적·IK)가 머리 본을 움직인 뒤에 값을 넣도록 실행 순서를 뒤로 미뤘다.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10000)]
    public class ToonFaceRig : MonoBehaviour
    {
        [Header("빛: 이 표식이 바라보는 방향으로 빛이 나아간다 (회전으로 조절)")]
        public Transform lightTransform;

        [Header("명암선")]
        [Tooltip("클수록 명암선이 얼굴 안쪽(빛 쪽)으로 들어온다. 0.25 기본, 0.45 는 레퍼런스처럼 얼굴을 많이 덮는다.")]
        [Range(-0.6f, 0.9f)] public float linePosition = -0.20f;
        [Tooltip("명암선 경계의 부드러움. 작을수록 딱 끊긴다.")]
        [Range(0.002f, 0.1f)] public float lineSoftness = 0.015f;
        [Tooltip("턱으로 당기기. 0 이면 세로 한 줄, 클수록 명암선 아래쪽이 턱 끝까지 안쪽으로 휘어 들어온다.")]
        [Range(0f, 1.5f)] public float chinTilt = 1.0f;
        [Tooltip("휘는 구간. 작을수록 턱 가까이에서 급하게 꺾이고(지수함수의 무릎이 아래로), 클수록 위에서부터 완만하게 휜다.")]
        [Range(0.05f, 1f)] public float chinBend = 0.35f;
        [Tooltip("코끝에서 턱 끝까지 높이 (미터). 미리보기 생성기가 모델에서 찾아 채운다.")]
        public float chinScale = 0.06f;

        [Header("코 음영 (콧구멍 바로 위, 콧방울 윗면. 코끝 주변만 원본 노멀로)")]
        public bool noseShadow = true;
        public Transform noseTip;
        [Tooltip("클수록 코 그림자가 콧대 옆면을 따라 위로 길어진다. 미리보기 생성기가 캐릭터마다 정한다: 콧대가 선 코는 0(콧구멍 바로 위 물방울), 평평한 코는 그림자가 보일 만큼 올린다(유니티짱 약 0.25, 콧선 옆 반달).")]
        [Range(-0.3f, 0.9f)] public float noseThreshold = 0f;
        [Tooltip("코 그림자 진하기. 1 이면 얼굴 그림자와 같은 톤, 작을수록 연하다.")]
        [Range(0f, 1f)] public float noseStrength = 0.6f;
        [Tooltip("코 영역 반경: x 좌우, y 위(콧대), z 아래, w 뒤쪽 깊이 (미터). 좌우가 좁으면 콧방울 쪽 그림자가 잘려 콧대 옆에 뜬다. 미리보기 생성기가 눈 사이 거리와 눈~코끝 높이로 채운다.")]
        public Vector4 noseRadius = new Vector4(0.039f, 0.035f, 0.015f, 0.037f);

        public enum NoseFigure { [InspectorName("초승달")] Crescent = 0, [InspectorName("세모")] Triangle = 1, [InspectorName("둥근 사다리꼴 (원래 그림자)")] RoundedQuad = 2, [InspectorName("물방울 (레퍼런스)")] Teardrop = 3 }

        [Header("코 도형 (코가 평평한 캐릭터: 원본 노멀 대신 도형)")]
        [Tooltip("미리보기 생성기가 코가 평평한 캐릭터(유니티짱)만 켠다. 원본 노멀 그림자는 큰 메시 면을 따라 꺾여 모서리가 각졌다. 켜면 위의 임계값·영역 대신 이 도형을 쓴다.")]
        public bool noseHalfMoon = false;
        [Tooltip("둥근 사다리꼴: 원래 원본 노멀 그림자의 윤곽을 사각형으로 줄이고 모서리를 둥글게. 세모: 콧선 쪽 세로 변 + 코끝 높이의 밑변, 꼭짓점은 위. 초승달: 두 호가 위아래 끝에서 만나는 모양.")]
        public NoseFigure noseFigure = NoseFigure.RoundedQuad;
        [Tooltip("둥근 사다리꼴일 때만: 모서리 둥글기. 0 이면 각진 사각형, 1 이면 거의 둥근 덩어리.")]
        [Range(0f, 1f)] public float quadRound = 0.5f;
        [Tooltip("둥근 사다리꼴 꼭짓점 0·1 (그림자 쪽 가로, 세로: 코끝 기준 미터). 미리보기 생성기가 원래 그림자 윤곽에서 잰다.")]
        public Vector4 quadA;
        [Tooltip("둥근 사다리꼴 꼭짓점 2·3")]
        public Vector4 quadB;
        [Tooltip("물방울일 때만: 크기 배율 (콧대 선과 아래 끝을 기준으로 전체를 키운다)")]
        [Range(0.5f, 2f)] public float dropScale = 1f;
        [Tooltip("물방울일 때만: 길이 배율 (뾰족한 위 끝을 콧대 쪽으로 늘인다)")]
        [Range(0.5f, 2f)] public float dropLength = 1f;
        [Tooltip("물방울 (61번): 아래 원 중심 u'·v, 위 끝 원 중심 u'·v (그림자 쪽 가로, 코끝 기준 세로, 미터). 미리보기 생성기가 채운다.")]
        public Vector4 dropA;
        [Tooltip("물방울: 아래 원 반지름, 위 끝 반지름, 콧대 선 가로 위치 (미터)")]
        public Vector4 dropR;
        [Tooltip("초승달일 때만: 기준 빛에서 가장 두꺼운 곳의 두께 (바깥 폭 대비). 0 이면 없음, 1 이면 반달. 빛이 정면으로 오면 얇아지고 옆으로 갈수록 두꺼워진다.")]
        [Range(0f, 1f)] public float moonThickness = 0.5f;
        [Tooltip("초승달일 때만: 길이 배율. 아래 끝(코끝 높이)은 두고 콧대 쪽으로 늘인다. 1 이면 원래 그림자 높이.")]
        [Range(1f, 3f)] public float moonStretch = 1f;
        [Tooltip("초승달일 때만: 바깥 폭 배율. 콧선 쪽 끝은 두고 바깥(뺨 쪽)으로 넓힌다. 두께만 1 가까이 올리면 반달이 되니, 두껍게 하려면 폭과 같이 올릴 것.")]
        [Range(0.5f, 2.5f)] public float moonWidth = 1f;
        [Tooltip("초승달일 때만: 가장 두꺼운 곳의 위치. 음수면 아래(코끝 쪽)가 두껍고 위로 가늘게 빠진다, 0 이면 가운데, 양수면 위가 두껍다.")]
        [Range(-0.8f, 0.8f)] public float moonBulge = 0f;
        [Tooltip("x 코 중심선에서 콧선 쪽 기준선(세모의 세로 변, 초승달의 두 끝)까지, y 중심 높이(코끝 기준, 위가 +), z 폭(세모의 밑변), w 반높이 (미터). 세모의 밑변 폭은 빛이 옆으로 갈수록 넓어진다(최대 1.5배).")]
        public Vector4 moonShape = new Vector4(0.0008f, -0.0012f, 0.0059f, 0.0052f);
        [Tooltip("x 기준 빛의 옆 성분(이때 두께가 위 값 그대로), y 코끝 뒤로 얼마까지 그리는지 (미터)")]
        public Vector4 moonParam = new Vector4(0.57f, 0.02f, 0f, 0f);

        [Header("눈 피하기 (명암선이 눈을 가르지 않고 바깥으로 돌아 나간다)")]
        public bool avoidEyes = false;
        [Range(0f, 1f)] public float eyeAvoidStrength = 1f;
        public Transform eyeA, eyeB;
        [Tooltip("눈 영역 반경: x 좌우, y 위아래, z 뒤쪽 깊이 (미터)")]
        public Vector3 eyeRadius = new Vector3(0.03f, 0.024f, 0.05f);

        [Header("림 (빛 쪽 윤곽선 안쪽을 살짝 밝게)")]
        [Range(0f, 1f)] public float rim = 0.3f;

        [Header("피부 그림자 색 (얼굴 재질에만, 텍스처에 곱하는 리니어 값)")]
        [Tooltip("기본은 원래 회보라(0.72, 0.66, 0.70). 58번에서 살구빛(0.86, 0.62, 0.60)·분홍빛(0.84, 0.60, 0.68)을 시험했지만 사용자가 원래 색을 유지하기로 했다(59번).")]
        public Color skinShadow = DefaultSkinShadow;
        public static readonly Color DefaultSkinShadow = new Color(0.72f, 0.66f, 0.70f, 1f);

        [Header("영역 표시 (코 빨강, 눈 파랑)")]
        public bool showRegions = false;

        [Header("외곽선 (화면공간 소벨 피처 켜고 끄기)")]
        public bool outline = true;
        public UnityEngine.Rendering.Universal.ScriptableRendererFeature outlineFeature;
        [Tooltip("변환 도구가 켠다. 켜면 이 리그가 켜질 때와 인스펙터에서 값을 바꿀 때, 아래 선 값을 외곽선 피처에 넣고 피처를 켠다 (프리팹이 선 값을 들고 다닌다). 외곽선은 화면 전체에 거는 효과라 캐릭터마다 다르게 할 수 없다: 값이 다른 리그가 여럿이면 마지막에 켜지거나 바뀐 리그의 값이 쓰인다. 렌더러 피처에서 꺼도 리그가 켜지거나 값이 바뀌면 다시 켜진다 — 계속 끄려면 모든 리그의 외곽선(outline)을 끈다. 리그가 직접 저장하지는 않지만, 렌더러 에셋을 다른 이유로 저장하면 넣은 값도 함께 기록된다.")]
        public bool driveOutlineSettings = false;
        [Range(0f, 1f)] public float outlineDarkness = 0.35f;
        [Range(0.5f, 4f)] public float outlineThickness = 1.0f;
        [Range(0f, 40f)] public float outlineDepthScale = 12f;
        [Range(0f, 8f)] public float outlineNormalScale = 0.8f;
        [Range(0f, 1f)] public float outlineThreshold = 0.30f;
        [Tooltip("얼굴이 화면에서 작아지면 얼굴 안쪽 선을 지우고 실루엣 선만 남긴다 (변환한 프리팹에만 적용). 픽셀 하나가 덮는 실제 크기(미터)가 x 를 넘으면 지우기 시작해 y 에서 다 지운다. 변환 도구가 얼굴 크기에 맞춰 채운다. y 가 0 이면 끔.")]
        public Vector2 innerLineFade = Vector2.zero;

        [Header("재질 (미리보기 생성기가 채운다)")]
        public List<Material> faceMaterials = new List<Material>();
        public List<Material> otherMaterials = new List<Material>();
        public Transform faceCenter;

        [Header("렌더러 (변환 도구가 채운다)")]
        [Tooltip("채워져 있으면 재질 에셋이 아니라 렌더러마다 따로 값을 넣는다. 같은 캐릭터를 씬에 여러 개 놓아도 서로 값을 덮어쓰지 않는다.")]
        public List<Renderer> faceRenderers = new List<Renderer>();
        public List<Renderer> otherRenderers = new List<Renderer>();
        [Tooltip("몸·옷의 명암 경계 위치 (변환한 프리팹에만 적용). 클수록 그림자가 넓어진다. 0.2 는 가장자리에만 살짝, 0.35 는 빛 반대쪽 3분의 1쯤.")]
        [Range(0f, 0.9f)] public float bodyShadow = 0.35f;
        [Tooltip("몸·옷의 그림자 색 (변환한 프리팹의 얼굴 아닌 메시에만, 텍스처에 곱하는 리니어 값). 기본은 피부 그림자와 같은 회보라. 어두운 옷에서 명암이 잘 안 보이면 더 어둡게. 얼굴과 몸이 한 메시인 캐릭터의 몸에는 적용되지 않는다(피부 그림자 색을 쓴다).")]
        public Color bodyShadowColor = DefaultSkinShadow;
        [Tooltip("변환 도구가 채운다: 이 프리팹을 만든 원본 모델의 GUID. 이름이 같은 다른 모델이 같은 폴더를 덮어쓰지 않게 하는 데 쓴다.")]
        public string sourceModelGuid = "";
        [HideInInspector] public List<string> generatedAssets = new List<string>();   // 변환 도구가 만든 메시·재질·텍스처의 GUID. 다시 변환할 때 이 목록에 있는 것만 정리한다

        void OnEnable() { ApplyOutline(); Apply(); }
        void OnValidate() { if (isActiveAndEnabled && gameObject.scene.IsValid()) { ApplyOutline(); Apply(); } }   // 프리팹 에셋 자체(씬 밖)에서는 넣을 대상이 없다
        void Update() { if (!Application.isPlaying) Apply(); }       // 편집 모드: 씬이 바뀔 때(표식 이동 등)만 불린다
        void LateUpdate() { if (Application.isPlaying) Apply(); }    // 플레이 중: Animator 가 본을 움직인 뒤에 넣어야 얼굴 명암이 한 프레임 늦지 않는다

        // 값을 넣을 곳: 재질 에셋(미리보기 씬) 또는 렌더러별 프로퍼티 블록(변환한 프리팹).
        struct Sink
        {
            public Material m; public MaterialPropertyBlock b;
            public void F(string n, float v) { if (m != null) m.SetFloat(n, v); else b.SetFloat(n, v); }
            public void V(string n, Vector4 v) { if (m != null) m.SetVector(n, v); else b.SetVector(n, v); }
        }
        MaterialPropertyBlock _block;
        Light _sun;
        float _nextSunSearch;
        Vector3 _lastLight = new Vector3(0.520f, 0.423f, 0.743f);   // 테스트 리그 기본값 (방위 35도, 고도 25도)

        /// <summary>
        /// 빛이 오는 쪽 방향. '빛' 표식이 있으면 그것을, 없으면 씬의 디렉셔널 라이트를 따른다(게임 씬에 놓았을 때).
        /// 디렉셔널 라이트가 여럿이면 RenderSettings.sun, 없으면 가장 센 것. 하나도 없으면 마지막으로 쓴 방향(처음에는 테스트 리그 기본값)을 유지한다.
        /// </summary>
        public Vector3 LightDirection()
        {
            if (lightTransform != null) return -lightTransform.forward;
            if (_sun == null || !_sun.isActiveAndEnabled || _sun.type != LightType.Directional)
            {
                _sun = null;
                // 디렉셔널 라이트가 없는 씬(실내)에서 매 프레임 전체 검색을 하지 않도록, 플레이 중에는 0.5초에 한 번만 찾는다
                if (!Application.isPlaying || Time.unscaledTime >= _nextSunSearch)
                {
                    _nextSunSearch = Time.unscaledTime + 0.5f;
                    var s = RenderSettings.sun;
                    if (s != null && s.isActiveAndEnabled && s.type == LightType.Directional) _sun = s;
                    else
                    {
                        float best = -1f;
                        foreach (var li in FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                            if (li.type == LightType.Directional && li.isActiveAndEnabled && li.intensity > best) { _sun = li; best = li.intensity; }
                    }
                }
            }
            if (_sun != null) _lastLight = -_sun.transform.forward;
            return _lastLight.normalized;
        }

        /// <summary>
        /// 프리팹 인스턴스의 배율. 미터 단위 값(코·눈 반경, 턱 높이, 코 도형)은 변환할 때의 크기라서,
        /// 게임에서 배율을 바꿔 놓으면 같이 곱해야 얼굴에 맞는다. 변환 도구는 리그의 월드 스케일을 1 로 맞춰 두므로 이 값이 곧 배율이다.
        /// </summary>
        /// <summary>시험용: 켜면 프리팹 배율을 반영하지 않는다 (배율 시험의 대조 렌더). 저장되지 않는다.</summary>
        [System.NonSerialized] public bool ignoreInstanceScale;

        float LengthScale()
        {
            if (ignoreInstanceScale) return 1f;
            var s = transform.lossyScale;
            float k = (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f;
            return Mathf.Abs(k - 1f) < 1e-4f ? 1f : k;
        }

        /// <summary>
        /// 변환한 프리팹(driveOutlineSettings)에서만: 외곽선 피처를 켜고 선 값을 넣는다. 켜질 때와 인스펙터에서 값을 바꿀 때만 부른다.
        /// 매 프레임 넣으면 사용자가 렌더러에서 피처를 꺼도 되살아나고, 값이 다른 리그끼리 프레임마다 덮어쓴다.
        /// </summary>
        public void ApplyOutline()
        {
            if (!driveOutlineSettings || outlineFeature == null) return;
            if (!outline) { if (outlineFeature.isActive) outlineFeature.SetActive(false); return; }   // 변환한 프리팹에서 끄면 피처도 끈다 (인스펙터 토글이 바로 보이게. 피처는 씬에 하나라 다른 리그에도 영향)
            if (!outlineFeature.isActive) outlineFeature.SetActive(true);
            var sf = outlineFeature as SobelOutlineFeature;
            if (sf == null) return;
            var st = sf.settings;
            st.darkness = outlineDarkness; st.thickness = outlineThickness; st.depthScale = outlineDepthScale;
            st.normalScale = outlineNormalScale; st.threshold = outlineThreshold;
        }

        static readonly List<Material> _slotMats = new List<Material>();

        /// <summary>현재 값을 모든 재질(또는 렌더러)에 밀어 넣는다.</summary>
        public void Apply()
        {
            Vector3 up = transform.up, fwd = transform.forward;
            Vector3 l = LightDirection();
            // 미리보기 씬: 표식 하나가 씬 전체의 외곽선을 켜고 끈다. 변환한 프리팹은 ApplyOutline 에서 따로 한다.
            if (outlineFeature != null && !driveOutlineSettings && outlineFeature.isActive != outline) outlineFeature.SetActive(outline);

            foreach (var m in otherMaterials) if (m != null) m.SetVector("_LightDir", l);
            foreach (var m in faceMaterials) { if (m != null) PushFace(new Sink { m = m }, up, fwd, l, 1f); }

            if (faceRenderers.Count == 0 && otherRenderers.Count == 0) return;
            if (_block == null) _block = new MaterialPropertyBlock();
            float k = LengthScale();
            var bodyBand = new Vector4(bodyShadowColor.r, bodyShadowColor.g, bodyShadowColor.b, 1f);
            // 안쪽 선이 뭉치는 거리는 선 두께에 비례해 가까워진다 (소벨의 표본 간격이 두께 픽셀)
            // 두께는 피처에 실제로 들어 있는 값을 읽는다 (다른 리그나 사용자가 바꿨을 수 있다)
            var sfNow = outlineFeature as SobelOutlineFeature;
            float thick = (sfNow != null && sfNow.isActive) ? sfNow.settings.thickness : outlineThickness;
            float fadeK = k / Mathf.Max(thick, 1f);
            var fade = new Vector4(innerLineFade.x * fadeK, innerLineFade.y * fadeK, 0f, 0f);
            foreach (var r in otherRenderers)
            {
                if (r == null) continue;
                r.GetPropertyBlock(_block); PushBody(_block, l, bodyBand); r.SetPropertyBlock(_block);
            }
            foreach (var r in faceRenderers)
            {
                if (r == null) continue;
                r.GetSharedMaterials(_slotMats);
                if (_slotMats.Count <= 1)
                {
                    // 얼굴 재질의 _Stop2 는 얼굴 밖(목 아래·손)의 일반 밴드에만 쓰인다. 얼굴 안은 명암선이 정한다.
                    r.GetPropertyBlock(_block); PushFace(new Sink { b = _block }, up, fwd, l, k); _block.SetFloat("_Stop2", bodyShadow);
                    _block.SetVector("_InnerLineFade", fade);
                    r.SetPropertyBlock(_block);
                    continue;
                }
                // 재질 칸이 여럿인 얼굴 메시(얼굴 + 속눈썹 카드 등): 얼굴 재질 칸에만 얼굴 값을, 나머지 칸에는 몸 값을 넣는다.
                for (int mi = 0; mi < _slotMats.Count; mi++)
                {
                    var sm = _slotMats[mi];
                    bool faceSlot = sm != null && sm.HasProperty("_FaceWeightOn") && sm.GetFloat("_FaceWeightOn") > 0.5f;
                    r.GetPropertyBlock(_block, mi);
                    if (faceSlot) { PushFace(new Sink { b = _block }, up, fwd, l, k); _block.SetFloat("_Stop2", bodyShadow); _block.SetVector("_InnerLineFade", fade); }
                    else PushBody(_block, l, bodyBand);
                    r.SetPropertyBlock(_block, mi);
                }
            }
        }

        void PushBody(MaterialPropertyBlock b, Vector3 l, Vector4 bodyBand)
        {
            b.SetVector("_LightDir", l); b.SetFloat("_Stop2", bodyShadow); b.SetVector("_Band1", bodyBand);
        }

        // k: 길이 배율. 재질 에셋 경로(미리보기 씬)는 늘 1, 변환한 프리팹은 인스턴스의 배율.
        void PushFace(Sink s, Vector3 up, Vector3 fwd, Vector3 l, float k)
        {
            s.V("_LightDir", l);
            s.F("_FaceCurve", 1f);
            s.V("_FaceUp", up);
            s.V("_FaceForward", fwd);
            if (faceCenter != null) s.V("_FaceCenter", faceCenter.position);
            s.F("_CurveStop", linePosition);
            s.F("_CurveSoft", lineSoftness);
            s.F("_CurveTilt", chinTilt);
            s.F("_ChinScale", chinScale * k);
            s.F("_ChinBend", chinBend);

            s.F("_NoseShade", (noseShadow && noseTip != null) ? noseStrength : 0f);
            if (noseTip != null) s.V("_NoseTip", noseTip.position);
            s.V("_NoseRadius", noseRadius * k);
            s.F("_NoseStop", noseThreshold);
            s.F("_NoseMoon", (noseShadow && noseHalfMoon && noseTip != null) ? 1f : 0f);
            s.V("_NoseMoonShape", (noseFigure == NoseFigure.Crescent ? StretchedMoonShape() : moonShape) * k);
            s.V("_NoseMoonParam", new Vector4(moonParam.x, moonParam.y * k, moonThickness, (float)(int)noseFigure));
            s.V("_NoseMoonExtra", new Vector4(moonBulge, 0f, 0f, 0f));
            if (noseFigure == NoseFigure.Teardrop)
            {
                Vector4 da, dr;
                DropParams(dropA, dropR, dropScale, dropLength, out da, out dr);
                s.V("_NosePolyA", da * k); s.V("_NosePolyParam", dr * k);
            }
            if (noseFigure == NoseFigure.RoundedQuad)
            {
                Vector4 sa, sb, sp;   // 둥근 사다리꼴 셰이더 값
                RoundQuadParams(quadA, quadB, quadRound, out sa, out sb, out sp);
                s.V("_NosePolyA", sa * k); s.V("_NosePolyB", sb * k); s.V("_NosePolyParam", sp * k);
            }

            s.F("_EyeAvoid", (avoidEyes && eyeA != null && eyeB != null) ? eyeAvoidStrength : 0f);
            if (eyeA != null) s.V("_EyeA", eyeA.position);
            if (eyeB != null) s.V("_EyeB", eyeB.position);
            s.V("_EyeRadius", new Vector4(eyeRadius.x, eyeRadius.y, eyeRadius.z, 0f) * k);

            s.F("_RimLight", rim);
            s.V("_Band1", new Vector4(skinShadow.r, skinShadow.g, skinShadow.b, 1f));
            s.F("_NoseDebug", showRegions ? 1f : 0f);
        }

        void OnDrawGizmosSelected()
        {
            // 코·눈 영역을 타원으로 그려서 어디를 잡았는지 보이게 한다.
            // 변환한 프리팹은 배율을 곱해 그린다 (셰이더에 넣는 값과 같게). 표식을 중심으로 그림만 키운다.
            float gk = (faceRenderers.Count > 0 || otherRenderers.Count > 0) ? LengthScale() : 1f;
            var gm = Gizmos.matrix;
            Gizmos.color = new Color(1f, 0.35f, 0.35f, 0.9f);
            if (noseTip != null)
            {
                Gizmos.matrix = Matrix4x4.Translate(noseTip.position) * Matrix4x4.Scale(Vector3.one * gk) * Matrix4x4.Translate(-noseTip.position);
                if (!noseHalfMoon) DrawEllipse(noseTip.position, noseRadius.x, noseRadius.y, noseRadius.z); else DrawHalfMoon();
            }
            Gizmos.color = new Color(0.35f, 0.6f, 1f, 0.9f);
            if (eyeA != null) { Gizmos.matrix = Matrix4x4.Translate(eyeA.position) * Matrix4x4.Scale(Vector3.one * gk) * Matrix4x4.Translate(-eyeA.position); DrawEllipse(eyeA.position, eyeRadius.x, eyeRadius.y, eyeRadius.y); }
            if (eyeB != null) { Gizmos.matrix = Matrix4x4.Translate(eyeB.position) * Matrix4x4.Scale(Vector3.one * gk) * Matrix4x4.Translate(-eyeB.position); DrawEllipse(eyeB.position, eyeRadius.x, eyeRadius.y, eyeRadius.y); }
            Gizmos.matrix = gm;
            if (lightTransform != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawRay(lightTransform.position, lightTransform.forward * 0.15f);
            }
        }

        /// <summary>코 초승달을 지금 빛 기준으로 그린다 (셰이더와 같은 식: 그림자 쪽, 안쪽 호만 빛을 따라 움직인다).</summary>
        void DrawHalfMoon()
        {
            Vector3 right = transform.right, up = transform.up, fwd = transform.forward;
            Vector3 l = LightDirection();
            Vector3 lh = l - up * Vector3.Dot(l, up);
            if (lh.sqrMagnitude < 1e-8f) return;
            lh.Normalize();
            float side = Vector3.Dot(lh, right);
            float sgn = side >= 0f ? -1f : 1f;
            float cosR = Mathf.Sqrt(Mathf.Clamp01(1f - moonParam.x * moonParam.x));
            float fL = (1f - Vector3.Dot(lh, fwd)) / Mathf.Max(1f - cosR, 1e-3f);
            Vector3 c = noseTip.position + right * (sgn * moonShape.x) + up * moonShape.y;
            if (noseFigure == NoseFigure.Teardrop)
            {
                // 물방울: 아래 원 중심에서 방향마다 경계까지 이분 탐색해서 윤곽을 그린다 (볼록 도형이라 가능, 셰이더와 같은 값)
                Vector4 da, dr;
                DropParams(dropA, dropR, dropScale, dropLength, out da, out dr);
                Vector2 pa = new Vector2(da.x, da.y), pb = new Vector2(da.z, da.w);
                float sD = Mathf.Clamp(fL, 1e-3f, 1.5f), u0d = dr.z;
                System.Func<Vector2, Vector3> toWD = q => noseTip.position + right * (sgn * (u0d + (q.x - u0d) * sD)) + up * q.y;
                const int ND = 48;
                Vector3 prevD = Vector3.zero;
                for (int i = 0; i <= ND; i++)
                {
                    float ang = i * Mathf.PI * 2f / ND;
                    Vector2 dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                    float lo = 0f, hi = (pb - pa).magnitude + dr.x * 2f;
                    for (int it = 0; it < 24; it++) { float mid = 0.5f * (lo + hi); if (SdDrop(pa + dir * mid, pa, pb, dr.x, dr.y) < 0f) lo = mid; else hi = mid; }
                    Vector3 pw = toWD(pa + dir * lo);
                    if (i > 0) Gizmos.DrawLine(prevD, pw);
                    prevD = pw;
                }
                return;
            }
            if (noseFigure == NoseFigure.RoundedQuad)
            {
                // 둥근 사다리꼴: 모서리를 둥글게 줄인 윤곽을 선분으로 근사해서 그린다 (셰이더와 같은 값)
                Vector4 sa, sb, sp;
                RoundQuadParams(quadA, quadB, quadRound, out sa, out sb, out sp);
                var s = new[] { new Vector2(sa.x, sa.y), new Vector2(sa.z, sa.w), new Vector2(sb.x, sb.y), new Vector2(sb.z, sb.w) };
                float sL = Mathf.Clamp(fL, 1e-3f, 1.5f), r = sp.x, uOut = sp.y;
                System.Func<Vector2, Vector3> toW = q => noseTip.position + right * (sgn * (uOut - (uOut - q.x) * sL)) + up * q.y;
                bool ccw = Cross2(s[1] - s[0], s[2] - s[1]) > 0f;
                for (int i = 0; i < 4; i++)
                {
                    Vector2 p = s[i], n = s[(i + 1) % 4], e = (n - p).normalized;
                    Vector2 nOut = ccw ? new Vector2(e.y, -e.x) : new Vector2(-e.y, e.x);   // 바깥쪽 법선
                    Gizmos.DrawLine(toW(p + nOut * r), toW(n + nOut * r));                 // 변
                    Vector2 e2 = (s[(i + 2) % 4] - n).normalized;
                    Vector2 nOut2 = ccw ? new Vector2(e2.y, -e2.x) : new Vector2(-e2.y, e2.x);
                    float a0 = Mathf.Atan2(nOut.y, nOut.x), a1 = Mathf.Atan2(nOut2.y, nOut2.x);
                    float da = Mathf.DeltaAngle(a0 * Mathf.Rad2Deg, a1 * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                    Vector2 prev = n + nOut * r;
                    for (int k = 1; k <= 6; k++)                                            // 모서리 호
                    {
                        float ang = a0 + da * k / 6f;
                        Vector2 q = n + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r;
                        Gizmos.DrawLine(toW(prev), toW(q)); prev = q;
                    }
                }
                return;
            }
            if (noseFigure == NoseFigure.Triangle)
            {
                // 세모: 콧선 쪽 세로 변, 밑변, 빗변 (셰이더와 같은 식)
                float w = moonShape.z * Mathf.Min(fL, 1.5f), h = moonShape.w;
                if (w < 1e-5f) return;
                Vector3 top = c + up * h, bottomIn = c - up * h, bottomOut = bottomIn + right * (sgn * w);
                Gizmos.DrawLine(top, bottomIn); Gizmos.DrawLine(bottomIn, bottomOut); Gizmos.DrawLine(bottomOut, top);
                return;
            }
            float thick = Mathf.Clamp01(moonThickness * fL);
            if (thick < 1e-3f) return;
            var ms = StretchedMoonShape();   // 길이 배율 적용
            Vector3 cm = noseTip.position + right * (sgn * ms.x) + up * ms.y;
            float a = ms.z, b = ms.w, aIn = a * (1f - thick);
            const int N = 32;
            Vector3 prevO = cm + up * b, prevI = prevO;
            for (int i = 1; i <= N; i++)
            {
                float q = 1f - 2f * i / N;                          // 위(+1)에서 아래(-1)로
                float prof = MoonProfile(q, moonBulge);
                Vector3 po = cm + right * (sgn * prof * a) + up * (q * b);     // 바깥 호
                Vector3 pi = cm + right * (sgn * prof * aIn) + up * (q * b);   // 안쪽 호
                Gizmos.DrawLine(prevO, po); Gizmos.DrawLine(prevI, pi);
                prevO = po; prevI = pi;
            }
        }

        static float Cross2(Vector2 a, Vector2 b) { return a.x * b.y - a.y * b.x; }

        /// <summary>
        /// 물방울 셰이더 값 (61번). 콧대 선(u0)과 아래 끝(아래 원의 맨 아래)을 기준으로 크기(scale)를 곱하고,
        /// 위 끝 높이만 길이(length)를 더 곱한다. 결과 a = (아래 원 중심, 위 끝 원 중심), r = (아래 반지름, 위 반지름, u0, 0).
        /// </summary>
        public static void DropParams(Vector4 dA, Vector4 dR, float scale, float length, out Vector4 a, out Vector4 r)
        {
            float u0 = dR.z, vBottom = dA.y - dR.x, s = Mathf.Max(scale, 0.01f), l = Mathf.Max(length, 0.01f);
            a = new Vector4(u0 + (dA.x - u0) * s, vBottom + (dA.y - vBottom) * s, u0 + (dA.z - u0) * s, vBottom + (dA.w - vBottom) * s * l);
            r = new Vector4(dR.x * s, dR.y * s, u0, 0f);
        }

        /// <summary>물방울까지의 부호 있는 거리 (셰이더 SdDrop 과 같은 식, 기즈모용).</summary>
        public static float SdDrop(Vector2 p, Vector2 pa, Vector2 pb, float ra, float rb)
        {
            p -= pa; pb -= pa;
            float h = Mathf.Max(Vector2.Dot(pb, pb), 1e-12f);
            var q = new Vector2(Vector2.Dot(p, new Vector2(pb.y, -pb.x)), Vector2.Dot(p, pb)) / h;
            q.x = Mathf.Abs(q.x);
            float b = ra - rb;
            var c = new Vector2(Mathf.Sqrt(Mathf.Max(h - b * b, 1e-12f)), b);
            float k = c.x * q.y - c.y * q.x, m = Vector2.Dot(c, q), n = Vector2.Dot(q, q);
            if (k < 0f) return Mathf.Sqrt(h * n) - ra;
            if (k > c.x) return Mathf.Sqrt(h * (n + 1f - 2f * q.y)) - rb;
            return m - ra;
        }

        /// <summary>초승달 길이 배율을 적용한 모양 (아래 끝은 그대로, 위로 늘인다).</summary>
        public Vector4 StretchedMoonShape()
        {
            float bottom = moonShape.y - moonShape.w, w = moonShape.w * Mathf.Max(moonStretch, 0.01f);
            return new Vector4(moonShape.x, bottom + w, moonShape.z * Mathf.Max(moonWidth, 0.01f), w);
        }

        /// <summary>초승달의 높이별 가로 비율 (셰이더와 같은 식). q 는 -1(아래 끝) ~ 1(위 끝), bulge 는 가장 두꺼운 곳의 위치.</summary>
        public static float MoonProfile(float q, float bulge)
        {
            float s = Mathf.Clamp(bulge, -0.9f, 0.9f);
            q = Mathf.Clamp(q, -1f, 1f);
            float w = (q - s) / (1f - s * q);
            return Mathf.Sqrt(Mathf.Clamp01(1f - w * w));
        }

        /// <summary>
        /// 둥근 사다리꼴 셰이더 값 (57번). 꼭짓점을 반지름 r 만큼 안쪽으로 줄인 사각형(sa, sb)과 r, 바깥 끝 가로 위치(sp).
        /// 줄인 사각형까지 거리 - r 로 그리면 변은 원래 자리 그대로, 모서리만 반지름 r 로 둥글어진다.
        /// round 는 0~1: 내접원 반지름 상한(면적 / 반둘레)의 비율. 너무 크면 사각형이 뒤집혀서 90% 로 제한.
        /// </summary>
        public static void RoundQuadParams(Vector4 qa, Vector4 qb, float round, out Vector4 sa, out Vector4 sb, out Vector4 sp)
        {
            var q = new[] { new Vector2(qa.x, qa.y), new Vector2(qa.z, qa.w), new Vector2(qb.x, qb.y), new Vector2(qb.z, qb.w) };
            float area2 = 0f, per = 0f, uMax = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector2 a = q[i], b = q[(i + 1) % 4];
                area2 += a.x * b.y - b.x * a.y; per += (b - a).magnitude; uMax = Mathf.Max(uMax, a.x);
            }
            float rMax = 0.5f * Mathf.Abs(area2) / Mathf.Max(0.5f * per, 1e-9f);
            float r = Mathf.Clamp01(round) * 0.9f * rMax;
            var s = new Vector2[4]; var o = new Vector2[4]; var d = new Vector2[4];
            for (int i = 0; i < 4; i++)
            {
                Vector2 e = q[(i + 1) % 4] - q[i];
                Vector2 nIn = area2 > 0f ? new Vector2(-e.y, e.x) : new Vector2(e.y, -e.x);   // 반시계 순서면 왼쪽이 안쪽
                o[i] = q[i] + nIn.normalized * r; d[i] = e;
            }
            for (int i = 0; i < 4; i++)
            {
                int j = (i + 3) % 4;                           // 이전 변과 이 변을 안쪽으로 옮긴 두 직선의 교점
                float den = Cross2(d[j], d[i]);
                s[i] = Mathf.Abs(den) < 1e-14f ? o[i] : o[j] + d[j] * (Cross2(o[i] - o[j], d[i]) / den);
            }
            sa = new Vector4(s[0].x, s[0].y, s[1].x, s[1].y);
            sb = new Vector4(s[2].x, s[2].y, s[3].x, s[3].y);
            sp = new Vector4(r, uMax, 0f, 0f);
        }

        void DrawEllipse(Vector3 c, float rx, float ryUp, float ryDown)
        {
            Vector3 right = transform.right, up = transform.up;
            const int N = 40;
            Vector3 prev = Vector3.zero;
            for (int i = 0; i <= N; i++)
            {
                float a = i * Mathf.PI * 2f / N;
                float s = Mathf.Sin(a);
                Vector3 p = c + right * (Mathf.Cos(a) * rx) + up * (s * (s >= 0f ? ryUp : ryDown));
                if (i > 0) Gizmos.DrawLine(prev, p);
                prev = p;
            }
        }
    }
}
