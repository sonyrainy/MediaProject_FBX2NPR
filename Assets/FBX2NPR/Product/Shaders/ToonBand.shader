// Blender 노드 구성과 1:1로 맞춘 툰 밴드 셰이더 + 탄젠트 공간 노멀 맵.
//
// 조명을 유니티 라이팅에서 받지 않고 _LightDir 로 직접 넘깁니다.
// 이 프로젝트 라이트는 색온도 5000K 에 강도 2 이고 URP 에셋에 톤매핑 볼륨이 물려 있어서,
// 하나씩 끄는 것보다 계산에 끼어들지 못하게 하는 편이 통제가 확실합니다.
//
// 노멀 맵은 Default 타입 · 무압축 · sRGB 끔으로 임포트한 것을 전제로 raw 디코드합니다.
// NormalMap 타입으로 넣으면 DXT5nm/BC5 스위즐 때문에 UnpackNormal 변종을 골라야 하고,
// 틀리면 그럴듯해 보이는 쓰레기가 나옵니다. 실험에서는 그 경로를 아예 피합니다.
Shader "NormalTest/ToonBand"
{
    Properties
    {
        _LightDir ("빛이 오는 방향 (월드)", Vector) = (0, 0.4226, 0.9063, 0)
        _Stop1    ("밴드1 임계값", Float) = 0.267
        _Stop2    ("밴드2 임계값", Float) = 0.487
        _Band0    ("밴드0 색 (리니어)", Vector) = (0.32, 0.30, 0.42, 1)
        _Band1    ("밴드1 색 (리니어)", Vector) = (0.72, 0.66, 0.70, 1)
        _Band2    ("밴드2 색 (리니어)", Vector) = (1.00, 0.96, 0.92, 1)
        // 서브컬처 얼굴 조명: 조명용 노멀을 얼굴 정면 벡터 쪽으로 섞어 얼굴 전체를 한 톤으로, 빛 반대쪽 실루엣 근처만 한 단계 어둡게.
        _FaceForward ("얼굴 정면 방향 (월드)", Vector) = (0, 0, 1, 0)
        _FaceCenter  ("얼굴 중심 (월드)", Vector) = (0, 0, 0, 0)
        _FaceFlat    ("조명 노멀을 정면으로 섞기", Range(0,1)) = 0
        _EdgeDark    ("가장자리 어둡게 (빛 반대쪽)", Range(0,1)) = 0
        _EdgeWidth   ("가장자리 폭 임계값", Float) = 0.55
        _EdgeTaper   ("가장자리 띠 끝 가늘어짐 (빛 반대쪽에서 멀어질수록 폭 감소)", Float) = 0.5
        _EdgeSoft    ("가장자리 띠 경계 부드럽기 (0이면 딱 끊김)", Float) = 0.05
        // 빛 쪽 윤곽선 안쪽을 살짝 밝게 (림라이트). 그림자 띠와 같은 구조, 방향만 반대.
        _RimLight    ("빛 쪽 가장자리 밝기 (0이면 끔)", Range(0,1)) = 0
        _RimWidth    ("빛 쪽 가장자리 폭 임계값", Float) = 0.92
        // 빛을 잘 받는 곳은 원본 노멀(코·뺨의 형태감), 어두워지는 곳은 정리된 노멀(얼룩 제거). UV3(스킨 메시는 UV4)에 심은 원본 노멀을 쓴다.
        _OrigBlend   ("빛 쪽은 원본 노멀 유지 (0이면 끔)", Range(0,1)) = 0
        _OrigLo      ("원본 유지 하한 (이 ndotl 아래는 정리된 노멀)", Float) = 0.25
        _OrigHi      ("원본 유지 상한 (이 ndotl 위는 원본 노멀)", Float) = 0.55
        // 서브컬처 얼굴 명암선 (46번): 정리된 노멀의 "수평 성분"만으로 명암을 가른다.
        // 빛의 고도를 무시하므로 경계가 이마에서 턱까지 끊김 없는 한 줄 곡선(자오선)이 된다.
        _FaceCurve   ("얼굴 명암선을 한 줄 곡선으로 (0이면 끔)", Range(0,1)) = 0
        _FaceUp      ("얼굴 위 방향 (월드)", Vector) = (0, 1, 0, 0)
        _CurveStop   ("명암선 위치 (수평 ndotl 임계값)", Float) = 0.25
        _CurveSoft   ("명암선 경계 부드럽기", Float) = 0.015
        // 49번: 명암선을 지수함수 꼴로. 위쪽은 옆면을 따라 거의 세로로 내려오다가 턱 가까이에서 급하게 휘어 턱 끝으로 모인다.
        // 턱 끝에서 임계값을 _CurveTilt 만큼 올리고, 위로 갈수록 exp 로 줄인다 (48번의 직선 기울기는 선이 곧은 사선이 됐다).
        _CurveTilt   ("턱으로 당기기 (0 세로 한 줄, 클수록 턱 끝까지 안쪽으로)", Range(0,1.5)) = 0
        _ChinScale   ("코끝에서 턱 끝까지 높이 (미터)", Float) = 0.06
        _ChinBend    ("휘는 구간 (턱 끝에서 코끝 방향, 작을수록 턱 가까이서 급하게 휨)", Range(0.05,1)) = 0.35
        // 50번: 기준점 명암선. 빛 반대쪽 눈꼬리(살짝 바깥)와 턱 끝 조금 옆 턱선을 반드시 지나도록 높이별 임계값을 정한다.
        // 눈꼬리 높이에서 시작해 그 위는 밝고, 볼을 타고 내려가다 턱 가까이에서 지수 꼴로 휜다.
        _AnchorOn     ("기준점 명암선 (눈꼬리 → 턱선)", Range(0,1)) = 0
        _AnchorFix    ("1 눈꼬리에 고정 / 0 빛을 따라 움직임", Range(0,1)) = 0
        _AnchorLight  ("기준 빛 방향 (따라 움직임: 이 빛일 때 선이 눈꼬리를 지남)", Vector) = (0.52, 0.42, 0.74, 0)
        _RadialCenter ("방사형 노멀 중심 (월드)", Vector) = (0, 0, 0, 0)
        // 기준점은 얼굴 표면 위의 점이어야 한다 (그 점의 노멀 = 중심에서 본 방향). A = +X 쪽, B = -X 쪽.
        _AnchorCA     ("눈꼬리 기준점 A (월드, 표면)", Vector) = (0, 0, 0, 0)
        _AnchorCB     ("눈꼬리 기준점 B (월드, 표면)", Vector) = (0, 0, 0, 0)
        _AnchorJA     ("턱선 기준점 A (월드, 표면)", Vector) = (0, 0, 0, 0)
        _AnchorJB     ("턱선 기준점 B (월드, 표면)", Vector) = (0, 0, 0, 0)
        _AnchorTopSlope ("눈꼬리 위 선의 기울기 (위로 갈수록 바깥으로)", Float) = 0.08
        _AnchorGain   ("빛 따라 이동량 (미터, 방위각 사인 1 당)", Float) = 0.09
        _AnchorSoft   ("선 경계 부드럽기 (미터)", Float) = 0.0008
        // 코 음영: 코끝 주변 타원 안에서만 원본 노멀로 단순한 그림자를 넣는다. 얼굴 다른 곳의 원본 요철은 쓰지 않는다.
        _NoseShade   ("코 음영 세기 (0이면 끔)", Range(0,1)) = 0
        _NoseTip     ("코끝 위치 (월드)", Vector) = (0, 0, 0, 0)
        _NoseRadius  ("코 영역 반경 (x 좌우, y 위, z 아래, w 뒤쪽 깊이)", Vector) = (0.02, 0.045, 0.012, 0.04)
        _NoseLineFade ("코 영역 안쪽 선 지우기 (리그가 넣음)", Range(0,1)) = 0
        _NoseStop    ("코 음영 임계값 (원본 노멀 수평 ndotl)", Float) = 0.30
        _NoseDebug   ("코(빨강)·눈(파랑) 영역 표시 (디버그)", Range(0,1)) = 0
        // 코 반달 (54번): 코가 평평한 캐릭터는 원본 노멀 대신 코끝 기준 얼굴 평면에 반달(곧은 변 + 반타원 호)을 그린다.
        _NoseMoon      ("코 반달 켜기 (평평한 코)", Range(0,1)) = 0
        _NoseMoonShape ("반달: x 코 중심선에서 곧은 변까지, y 중심 높이(코끝 기준), z 폭, w 반높이 (미터)", Vector) = (0.001, 0.005, 0.005, 0.008)
        _NoseMoonParam ("코 도형: x 기준 빛의 옆 성분, y 깊이 한계 (미터), z 초승달 두께 (0 없음 ~ 1 반달), w 모양 (0 초승달, 1 세모, 2 둥근 사다리꼴, 3 물방울)", Vector) = (0.57, 0.02, 0.5, 0)
        _NoseMoonExtra ("초승달: x 가장 두꺼운 곳 위치 (-0.9 아래 ~ 0.9 위, 0 가운데), y 코 옆 표면에서 방사형 노멀 중심까지 수평 거리 (미터)", Vector) = (0, 0.06, 0, 0)
        _NoseLitMoon   ("밝은 코 초승달: 명암선이 코를 넘은 뒤 그늘 속에 코 옆을 밝게 (리그가 넣음, 초승달 모양일 때만)", Range(0,1)) = 0
        // 둥근 사다리꼴 (57번): 원래 원본 노멀 그림자의 윤곽을 사각형으로 줄이고 모서리를 둥글게. 꼭짓점은 반지름만큼 안쪽으로 줄여 둔 값.
        _NosePolyA     ("둥근 사다리꼴 꼭짓점 0·1 (그림자 쪽 가로, 세로: 코끝 기준 m)", Vector) = (0, 0, 0, 0)
        _NosePolyB     ("둥근 사다리꼴 꼭짓점 2·3", Vector) = (0, 0, 0, 0)
        _NosePolyParam ("둥근 사다리꼴: x 모서리 반지름, y 바깥 끝 가로 위치 (m)", Vector) = (0, 0, 0, 0)
        // 눈 피하기 (47번): 눈 주변 타원 안에서는 명암선 임계값을 낮춰, 명암선이 눈을 가르지 않고 바깥으로 휘어 돈다.
        _EyeAvoid    ("눈 피하기 세기 (0이면 끔)", Range(0,1)) = 0
        _EyeA        ("눈 중심 A (월드)", Vector) = (0, 0, 0, 0)
        _EyeB        ("눈 중심 B (월드)", Vector) = (0, 0, 0, 0)
        _EyeRadius   ("눈 영역 반경 (x 좌우, y 위아래, z 뒤쪽 깊이)", Vector) = (0.025, 0.013, 0.05, 0)
        [NoScaleOffset] _NormalMap ("노멀 맵 (Default 타입, raw)", 2D) = "bump" {}
        _NormalStrength ("노멀 맵 강도", Range(0,1)) = 0
        // 알베도를 밴드에 곱합니다. 0 이면 클레이, 1 이면 텍스처 그대로.
        [NoScaleOffset] _Albedo ("알베도", 2D) = "white" {}
        _AlbedoOn   ("알베도 섞기", Range(0,1)) = 0
        _Posterize  ("알베도 색 단계 수 (채널별, 색틀어짐 주의)", Float) = 0
        _PosterizeLum ("알베도 밝기 단계 수 (색상 보존)", Float) = 0
        _Saturation ("알베도 채도", Range(0,2)) = 1
        _Lift       ("알베도 밝기 들어올리기", Range(0,1)) = 0
        // 사진 알베도의 고주파 디테일(모공, 구워진 잔음영)을 밉맵으로 지웁니다.
        _Flatten    ("알베도 디테일 지우기", Range(0,1)) = 0
        _FlattenMip ("지울 때 쓸 밉 단계", Range(0,8)) = 4
        [Toggle] _ShowNormal ("최종 노멀을 색으로", Float) = 0
        [Toggle] _UseOrigNormal ("선 검출에 원본 노멀 사용 (UV3)", Float) = 1
        // 스킨 메시용. UV3 에 실은 원본 노멀은 뼈를 따라 돌지 않습니다(스키닝은 위치·노멀·탄젠트만 변형합니다).
        // 고개를 돌리면 코 음영과 외곽선이 어긋나므로, 변환 도구는 원본 노멀을 노멀·탄젠트 기준 방향(탄젠트 공간)으로 UV4 에 실어 둡니다.
        // 켜면 원본 노멀을 UV3 대신 UV4 에서 읽어 지금의 노멀·탄젠트로 되돌립니다. 탄젠트는 진짜 탄젠트라 노멀맵도 쓸 수 있습니다.
        [Toggle] _OrigTangentSpace ("원본 노멀을 UV4(탄젠트 공간)에서 읽기 (스킨 메시)", Float) = 0
        // 얼굴과 몸이 한 메시·한 재질인 캐릭터용 (믹사모: 머리·목·손이 한 메시). 켜면 얼굴 명암선·가장자리 띠·림을
        // 정점의 얼굴 가중치(UV2.x, 변환 도구가 넣음)만큼만 적용하고, 나머지는 일반 밴드 명암으로 둡니다.
        [Toggle] _FaceWeightOn ("얼굴 명암을 얼굴 가중치(UV2.x)만큼만", Float) = 0
        // 머리카락·속눈썹처럼 가닥마다 선이 그어지면 안 되는 메시용.
        // 노멀 버퍼에 카메라 방향을 써서 내부 노멀 엣지를 0 으로 만들고, 실루엣 깊이 엣지만 남깁니다.
        // 실루엣 선만 쓰는 얼굴은 변환 도구가 렌더러에도 표시해 두므로(OriginalNormals.FlatLineLayer), 그 렌더러에서는 이 값을 꺼도 실루엣만 그립니다.
        [Toggle] _OutlineFlatNormal ("외곽선용 노멀을 카메라 방향으로 (실루엣만)", Float) = 0
        // 얼굴이 화면에서 작아지면 한 픽셀에 코·입의 굴곡이 통째로 들어가, 안쪽 선이 선이 아니라 갈색 덩어리로 칠해집니다 (전신 구도에서 실측).
        // 픽셀 하나가 덮는 실제 크기가 x 를 넘으면 외곽선용 노멀을 카메라 방향으로 섞기 시작해 y 에서 실루엣 선만 남깁니다. y 가 0 이면 끔.
        _InnerLineFade ("안쪽 선을 멀어지면 지우기: x 시작, y 끝 (픽셀 하나의 실제 크기, 미터)", Vector) = (0, 0, 0, 0)
        // 머리카락·속눈썹 카드는 형태가 텍스처 알파에 있습니다. 컷아웃 없이는 불투명 사각형으로 그려집니다.
        [Toggle] _Cutout ("알파 컷아웃", Float) = 0
        _Cutoff ("컷아웃 문턱", Range(0,1)) = 0.5
        // 발광 맵 (보석·불빛처럼 빛과 무관하게 밝은 부분). 변환 도구가 원본 재질에 발광 맵이 있으면 옮깁니다. 꺼져 있으면 예전과 같습니다.
        [Toggle] _EmissionOn ("발광 맵 더하기", Float) = 0
        [NoScaleOffset] _EmissionMap ("발광 맵", 2D) = "black" {}
        _EmissionColor ("발광 색 (리니어 배율)", Vector) = (1, 1, 1, 0)
        // URP 조명 모드 (4단계): 켜면 _LightDir 대신 씬의 메인 라이트(방향·색·받는 그림자)를 쓴다. 점·스폿 조명은 받지 않는다.
        // 끄면(기본) 예전과 똑같은 블렌더 대조 모드다. 명암의 모양(밴드 경계, 얼굴 명암선, 코 음영)은 두 모드가 같은 식을 쓴다.
        [Toggle] _UrpLighting ("URP 조명 받기 (0이면 블렌더 대조 모드)", Float) = 0
        _LightMaxLimit ("빛 밝기 상한 (색조는 두고 가장 밝은 채널을 이 값까지)", Range(0.1, 4)) = 1
        _ShadowReceive ("받는 그림자 세기 (몸)", Range(0,1)) = 1
        // 얼굴은 기본으로 받지 않는다: 코가 볼에 드리운 그림자가 그림자 맵 해상도대로 번진 얼룩처럼 보였다 (Ch07).
        _FaceShadowReceive ("받는 그림자 세기 (얼굴, 얼굴 가중치만큼)", Range(0,1)) = 0
        // 얼굴 SDF 맵 (FaceSdfBake 가 굽는다). 켜면 얼굴 명암을 규칙 대신 이 맵에서 읽는다.
        [Toggle(_FACE_SDF)] _UseFaceSdf ("얼굴 명암을 SDF 맵에서 읽기", Float) = 0
        [NoScaleOffset] _FaceSDF ("얼굴 SDF 맵", 2D) = "white" {}
        _FaceSdfRect ("SDF 맵이 덮는 UV 사각형 (xy 시작, zw 크기)", Vector) = (0, 0, 1, 1)
        _KeepLit ("명암 없이 밝게 (눈 메시)", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "ToonBand"
            // 블렌더 EEVEE 는 기본이 양면 렌더라 Cull Off 가 패리티에 맞습니다.
            // 그리고 NORMAL_EDIT 가 면을 뒤집는 문제를 겪은 뒤라, 컬링에 의존하지 않는 편이 안전합니다.
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // URP 조명 모드용. 블렌더 대조 모드는 이 키워드와 상관없이 같은 값을 낸다.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            // 점·스폿 조명은 안 받지만 이 키워드는 둔다: 포워드+ 에서 이것 없이 컴파일하면 GetMainLight 가 세기를
            // unity_LightData 에서 읽는데, 포워드+ 는 그 값을 채우지 않아 메인 라이트가 0 이 되고 URP 조명 모드가 검게 나왔다.
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma shader_feature_local_fragment _ _FACE_SDF
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            // Core.hlsl 은 Color.hlsl 을 끌고 오지 않습니다. LinearToSRGB 를 쓰려면 직접 넣어야 합니다.
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "OriginalNormals.hlsl"

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_Albedo);
            SAMPLER(sampler_Albedo);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            #include "ToonBandInput.hlsl"   // 재질 값 (네 패스가 같은 UnityPerMaterial 을 써야 SRP Batcher 를 탄다)
            #include "ToonFaceRule.hlsl"
            #include "FaceSDF.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 faceW      : TEXCOORD2;   // x = 얼굴 가중치 (변환 도구가 넣음, 없으면 0)
                float3 origOS     : TEXCOORD3;   // 임포트 때 심어 둔 원본 노멀
                float3 origTS     : TEXCOORD4;   // 변환 도구가 탄젠트 공간으로 실어 둔 원본 노멀 (_OrigTangentSpace)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float4 tangentWS  : TEXCOORD1;   // w 에 bitangent 부호
                float2 uv         : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                float3 origWS     : TEXCOORD4;
                float  faceW      : TEXCOORD5;
            };

            // URP 조명 모드: 빛 색의 색조는 두고 가장 밝은 채널만 _LightMaxLimit 까지 누른다.
            // 센 빛(이 프로젝트 라이트는 강도 2)에서도 밴드 색이 텍스처보다 밝게 날리지 않는다 (툰 셰이더들의 관례).
            float3 ToonTint(float3 col)
            {
                float m = max(col.r, max(col.g, col.b));
                return (m > _LightMaxLimit) ? col * (_LightMaxLimit / m) : col;
            }

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                // UV4 가 비어 있으면(예전 메시) UV3 을 씁니다.
                float3 origOS = (_OrigTangentSpace > 0.5 && dot(v.origTS, v.origTS) > 0.25) ? DecodeOriginalNormal(v.origTS, v.normalOS, v.tangentOS) : v.origOS;
                o.origWS     = TransformObjectToWorldNormal(origOS);
                o.normalWS   = TransformObjectToWorldNormal(v.normalOS);
                float3 tWS   = TransformObjectToWorldDir(v.tangentOS.xyz);
                // 음수 스케일이 걸린 오브젝트에서 bitangent 가 뒤집히는 것을 보정합니다.
                o.tangentWS  = float4(tWS, v.tangentOS.w * unity_WorldTransformParams.w);
                o.uv         = v.uv;
                o.faceW      = v.faceW.x;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                if (_Cutout > 0.5) clip(SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, i.uv).a - _Cutoff);

                float3 n = normalize(i.normalWS);

                if (_NormalStrength > 0.0001)
                {
                    // URP LitForwardPass 와 같은 구성입니다.
                    // 보간값을 정규화하지도, 탄젠트를 노멀에 재직교화하지도 않습니다.
                    // MikkTSpace 규약이 보간된 T, N 을 그대로 쓰라고 정하고 있어서,
                    // Gram-Schmidt 를 걸면 오히려 유니티 기본 셰이더와 다른 결과가 나옵니다.
                    float  sgn = i.tangentWS.w;
                    float3 bit = sgn * cross(i.normalWS.xyz, i.tangentWS.xyz);
                    float3x3 tangentToWorld = float3x3(i.tangentWS.xyz, bit, i.normalWS.xyz);

                    // Default 타입 + sRGB 끔 + 무압축으로 임포트한 것을 전제로 raw 디코드합니다.
                    // NormalMap 타입은 무압축이어도 (R=1, G=y, B=y, A=x) 로 스위즐하므로
                    // 여기에 rgb*2-1 을 쓰면 통째로 쓰레기가 됩니다.
                    float3 nTS = SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, i.uv).xyz * 2.0 - 1.0;
                    // xy 만 줄이는 것이 표준적인 강도 조절입니다. 0 이면 (0,0,1) 즉 완전 평탄.
                    nTS.xy *= _NormalStrength;

                    n = normalize(mul(nTS, tangentToWorld));
                }

                if (_ShowNormal > 0.5)
                {
                    return half4(n * 0.5 + 0.5, 1);
                }

                float3 l = normalize(_LightDir.xyz);
                // URP 조명 모드: 방향은 메인 라이트에서, 색과 받은 그림자는 아래 밴드에 쓴다.
                bool   urp = _UrpLighting > 0.5;
                float3 lightTint = 1.0;
                float  shadowAtten = 1.0;
                if (urp)
                {
                    Light ml = GetMainLight(TransformWorldToShadowCoord(i.positionWS), i.positionWS, half4(1, 1, 1, 1));
                    l = ml.direction;
                    lightTint = ToonTint(ml.color * ml.distanceAttenuation);
                    shadowAtten = ml.shadowAttenuation;
                }
                // 조명용 노멀. _FaceFlat 이 1 이면 얼굴 전체가 정면 벡터 하나로 조명돼 한 톤이 됩니다 (서브컬처 관례).
                float3 nLight = n;
                if (_OrigBlend > 0.0001)
                {
                    float3 no = normalize(i.origWS);
                    float ndlo = saturate(dot(no, l));
                    float wClean = 1.0 - smoothstep(_OrigLo, _OrigHi, ndlo);   // 빛을 잘 받는 곳은 원본, 어두워지는 곳은 정리된 노멀
                    nLight = normalize(lerp(no, n, lerp(1.0, wClean, _OrigBlend)));
                }
                float3 nl = normalize(lerp(nLight, normalize(_FaceForward.xyz), _FaceFlat));
                // 블렌더도 Fac 를 [0,1] 로 클램프하므로 여기서도 saturate 를 겁니다.
                float ndotl = saturate(dot(nl, l));

                float3 c = _Band0.rgb;
                if (ndotl >= _Stop1) c = _Band1.rgb;
                if (ndotl >= _Stop2) c = _Band2.rgb;
                // URP 조명 모드: 받은 그림자 안은 밝은 밴드가 그림자 밴드로 한 단 내려간다 (더 어두운 단은 그대로).
                // 그림자 맵 가장자리의 부드러운 구간 가운데에서 짧게 넘겨 툰처럼 경계를 세운다.
                float shBody = 1.0, shFace = 1.0;
                if (urp)
                {
                    shBody = smoothstep(0.35, 0.65, lerp(1.0, shadowAtten, _ShadowReceive));
                    shFace = smoothstep(0.35, 0.65, lerp(1.0, shadowAtten, _FaceShadowReceive));
                    c = lerp((ndotl >= _Stop1) ? _Band1.rgb : _Band0.rgb, c, shBody);
                }
                // 눈 메시는 자기 노멀로 명암을 받으면 피부에 그림자가 없는데 눈 위로만 띠가 지나가거나 눈이 세로로 갈라졌다.
                if (_KeepLit > 0.5) c = _Band2.rgb;

                // 서브컬처 얼굴 명암선 (46번). 45번(_OrigBlend)은 원본 노멀의 밝기를 기준으로 두 노멀을 섞어서,
                // 원본 노멀의 요철이 그대로 경계선에 옮겨 붙었다(입꼬리의 홈). 경계는 정리된 노멀 하나로만 정한다.
                // 수평 성분만 쓰므로 빛의 고도와 무관하게 이마에서 턱까지 한 줄 곡선이 되고, 턱 밑이 통째로 어두워지지 않는다.
                float noseDbg = 0.0;
                float eyeDbg = 0.0;
                float anchorDbg = 0.0;
                if (_FaceCurve > 0.5)
                {
                    float3 up = normalize(_FaceUp.xyz);
                    float3 lh = l - up * dot(l, up);
                    float lhl = length(lh);
                    float lit = 1.0;
                    if (lhl > 1e-4)
                    {
                        float3 lhN = lh / lhl;
                        // 얼굴 명암: 규칙으로 계산하거나(ToonFaceRule.hlsl), 구워 둔 SDF 맵을 읽는다(FaceSDF.hlsl). 재질의 _FACE_SDF 키워드가 고른다.
                    #if defined(_FACE_SDF)
                        FaceShade face = EvalFaceSdf(i.uv, i.positionWS, lhN, up);
                    #else
                        FaceShade face = EvalFaceRule(i.positionWS, n, i.origWS, lhN, up);
                    #endif
                        lit = min(face.lit, 1.0 - face.noseShade * _NoseShade);
                        lit = max(lit, face.noseLit * _NoseShade);   // 그림자 초승달과 같은 세기로 그늘을 걷어 낸다 (대칭)
                        noseDbg = face.noseDbg; eyeDbg = face.eyeDbg; anchorDbg = face.anchorDbg;
                    }
                    if (urp) lit = min(lit, shFace);
                    float3 cFace = lerp(_Band1.rgb, _Band2.rgb, lit);
                    // 얼굴 가중치를 쓰면 얼굴 밖(목 아래·손)은 위에서 구한 일반 밴드 명암으로 남는다. 끄면 예전과 똑같이 전부 얼굴 명암.
                    if (_FaceWeightOn > 0.5) c = lerp(c, cFace, saturate(i.faceW)); else c = cFace;
                }

                // 빛 반대쪽 실루엣 근처만 한 단계 어둡게. 시선 기준(N·V)이라 빛 각도와 무관하게 테두리에 붙습니다.
                if (_EdgeDark > 0.0001)
                {
                    float3 v = normalize(_WorldSpaceCameraPos - i.positionWS);
                    float edge = 1.0 - saturate(dot(n, v));
                    // side: 화면 평면에서 "얼굴 중심 → 이 픽셀" 방향이 "빛 반대 방향"과 얼마나 같은가.
                    // 노멀 대신 화면 위치를 쓰면 띠의 양 끝이 얼굴 윤곽선 위에서 닫힌다 (초승달의 두 끝이 실루엣에 붙음).
                    float3 rel  = i.positionWS - _FaceCenter.xyz;
                    float3 dirP = rel - v * dot(rel, v);
                    float3 oppP = -(l - v * dot(l, v));
                    float dl = length(dirP), ol = length(oppP);
                    float side = (dl > 1e-4 && ol > 1e-4) ? saturate(dot(dirP / dl, oppP / ol)) : 0.0;
                    // 띠의 안쪽 경계는 N·V 등고선이라 실루엣과 평행한 완만한 곡선이 된다.
                    // side 는 폭에만 관여: 빛 반대쪽에서 가장 넓고, 이마·턱 끝으로 갈수록 가늘어져 사라진다.
                    float w = _EdgeWidth + (1.0 - side) * _EdgeTaper;
                    // 딱 끊는 대신 smoothstep 으로 서서히 들어가게 해서 띠의 양 끝이 뾰족하게 사라지고 경계가 미세해진다.
                    float k = smoothstep(w, w + max(_EdgeSoft, 1e-4), edge);
                    if (_FaceWeightOn > 0.5) k *= saturate(i.faceW);
                    c = lerp(c, min(c, _Band1.rgb), k * _EdgeDark);
                }
                float kRim = 0.0;
                if (_RimLight > 0.0001)
                {
                    float3 v = normalize(_WorldSpaceCameraPos - i.positionWS);
                    float edge = 1.0 - saturate(dot(n, v));
                    float3 rel  = i.positionWS - _FaceCenter.xyz;
                    float3 dirP = rel - v * dot(rel, v);
                    float3 litP = (l - v * dot(l, v));
                    float dl = length(dirP), ll = length(litP);
                    float sideL = (dl > 1e-4 && ll > 1e-4) ? saturate(dot(dirP / dl, litP / ll)) : 0.0;
                    float wl = _RimWidth + (1.0 - sideL) * _EdgeTaper;
                    kRim = smoothstep(wl, wl + max(_EdgeSoft, 1e-4), edge) * _RimLight;
                    if (_FaceWeightOn > 0.5) kRim *= saturate(i.faceW);
                }

                // URP 조명 모드: 메인 라이트 색을 곱한다. 점·스폿 조명은 받지 않는다. 더해 보니 빛마다 얼굴에 경계선이 하나씩 더 생기고,
                // 램프 그림자가 앞머리째 이마에 찍혔다 (여러 광원 시험). 여러 광원 장면은 UTS·lilToon 쪽 몫으로 둔다.
                if (urp) c *= lightTint;
                if (_AlbedoOn > 0.0001)
                {
                    float3 a = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, i.uv).rgb;

                    // 사진 알베도의 고주파 성분이 선과 밝기 경쟁을 합니다.
                    // 밉맵 상위 단계를 섞으면 모공과 구워진 잔음영이 사라지고 큰 색면만 남습니다.
                    if (_Flatten > 0.0001)
                    {
                        float3 blur = SAMPLE_TEXTURE2D_LOD(_Albedo, sampler_Albedo, i.uv, _FlattenMip).rgb;
                        a = lerp(a, blur, _Flatten);
                    }

                    // 채도와 계단화는 눈이 보는 공간에서 거는 편이 예측 가능합니다.
                    float3 s = LinearToSRGB(a);

                    float lum = dot(s, float3(0.2126, 0.7152, 0.0722));
                    s = lerp(lum.xxx, s, _Saturation);
                    s = lerp(s, 1.0.xxx, _Lift);

                    // 채널별 계단화는 채널마다 다른 자리에서 끊겨 색이 틀어집니다.
                    // 밝기만 계단화하고 색상은 그대로 두는 쪽이 안전합니다.
                    if (_PosterizeLum >= 2.0)
                    {
                        float n = _PosterizeLum;
                        float l0 = max(dot(s, float3(0.2126, 0.7152, 0.0722)), 1e-3);
                        float q  = floor(saturate(l0) * n) / (n - 1.0);
                        s = saturate(s * (q / l0));
                    }
                    else if (_Posterize >= 2.0)
                    {
                        float n = _Posterize;
                        s = saturate(floor(saturate(s) * n) / (n - 1.0));
                    }

                    a = SRGBToLinear(s);
                    c *= lerp(1.0.xxx, a, _AlbedoOn);
                }
                // 림라이트는 알베도를 곱한 뒤에 얹는다 (텍스처 위에서도 보이게).
                if (kRim > 0.0001) c = lerp(c, saturate(c * 1.25 + 0.06), kRim);
                if (_EmissionOn > 0.5) c += SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, i.uv).rgb * _EmissionColor.rgb;
                if (_NoseDebug > 0.5) { c = lerp(c, float3(1.0, 0.0, 0.0), noseDbg * 0.6); c = lerp(c, float3(0.0, 0.3, 1.0), eyeDbg * 0.6); c = lerp(c, float3(0.0, 1.0, 0.0), anchorDbg); }

                return half4(c, 1);
            }
            ENDHLSL
        }

        // URP 가 깊이·노멀 프리패스에서 부르는 패스입니다.
        //
        // 여기서 일부러 '원본' 노멀을 내보냅니다. 화면에 칠할 때는 평탄화한 노멀을 쓰고,
        // 선을 찾을 때는 원본을 쓰는 겁니다. 그러면 평탄화로 지워진 형태가
        // 정확히 그 자리에 선으로 돌아옵니다. 원본 노멀은 UV3(물체 공간) 또는 UV4(탄젠트 공간, 스킨 메시)에 실어 둡니다.
        // 외곽선용 노멀을 만드는 식은 LineNormal.hlsl 에 있고, 외곽선 피처의 원본 노멀 패스(LineNormals.shader)도 같은 식을 씁니다.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex dnVert
            #pragma fragment dnFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 컷아웃용. 깊이·노멀 버퍼도 잘라야 외곽선이 카드 사각형이 아니라 속눈썹 모양을 따라갑니다.
            TEXTURE2D(_Albedo);
            SAMPLER(sampler_Albedo);

            #include "ToonBandInput.hlsl"

            #include "OriginalNormals.hlsl"
            #include "LineNormal.hlsl"

            struct dnAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;     // UV4 의 원본 노멀을 되돌릴 축 (스킨 메시)
                float3 origOS     : TEXCOORD3;   // 임포트 때 심어 둔 원본 노멀
                float3 origTS     : TEXCOORD4;   // 탄젠트 공간 원본 노멀 (_OrigTangentSpace)
                float2 uv         : TEXCOORD0;
                float2 faceW      : TEXCOORD2;   // x = 얼굴 가중치, y = 피부 메시 전체 1 (안쪽 선 지우기용). 변환 도구가 넣음, 없으면 0
            };

            struct dnVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float2 uv         : TEXCOORD1;
            };

            dnVaryings dnVert (dnAttributes v)
            {
                dnVaryings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                // UV4 가 비어 있으면(예전 메시) UV3 을 씁니다.
                float3 orig = (_OrigTangentSpace > 0.5 && dot(v.origTS, v.origTS) > 0.25) ? DecodeOriginalNormal(v.origTS, v.normalOS, v.tangentOS) : v.origOS;
                float3 src = (_UseOrigNormal > 0.5) ? orig : v.normalOS;
                // UV3 가 비어 있으면 (0,0,0) 이므로 그때는 메시 노멀로 되돌립니다.
                if (dot(src, src) < 1e-8) src = v.normalOS;
                o.normalWS = LineNormalWS(v.positionOS.xyz, src, v.faceW);
                return o;
            }

            half4 dnFrag (dnVaryings i) : SV_Target
            {
                if (_Cutout > 0.5) clip(SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, i.uv).a - _Cutoff);
                return half4(normalize(i.normalWS), 0.0);
            }
            ENDHLSL
        }

        // 그림자 드리우기. 이 패스가 없으면 게임 씬에서 캐릭터가 바닥에 그림자를 만들지 못합니다.
        // (툰 명암 자체는 _LightDir 로만 정하므로 그림자를 '받지'는 않습니다.)
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Off

            HLSLPROGRAM
            #pragma vertex scVert
            #pragma fragment scFrag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_Albedo);
            SAMPLER(sampler_Albedo);

            #include "ToonBandInput.hlsl"

            // URP 가 그림자 패스 때 넣어 주는 값 (디렉셔널은 방향, 점·스폿은 위치)
            float3 _LightDirection;
            float3 _LightPosition;

            struct scAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct scVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            scVaryings scVert (scAttributes v)
            {
                scVaryings o;
                float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                float3 nw = TransformObjectToWorldNormal(v.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 ld = normalize(_LightPosition - pw);
            #else
                float3 ld = _LightDirection;
            #endif
                float4 pc = TransformWorldToHClip(ApplyShadowBias(pw, nw, ld));
                o.positionCS = ApplyShadowClamping(pc);   // URP 와 같은 근평면 처리 (디렉셔널에만 적용)
                o.uv = v.uv;
                return o;
            }

            half4 scFrag (scVaryings i) : SV_Target
            {
                if (_Cutout > 0.5) clip(SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, i.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }

        // 깊이만 쓰는 프리패스. 외곽선 피처가 꺼져 DepthNormals 프리패스가 돌지 않는 구성에서도 깊이 텍스처에 캐릭터가 들어가게 합니다.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex doVert
            #pragma fragment doFrag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_Albedo);
            SAMPLER(sampler_Albedo);

            #include "ToonBandInput.hlsl"

            struct doAttributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct doVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            doVaryings doVert (doAttributes v)
            {
                doVaryings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            half4 doFrag (doVaryings i) : SV_Target
            {
                if (_Cutout > 0.5) clip(SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, i.uv).a - _Cutoff);
                return i.positionCS.z;
            }
            ENDHLSL
        }
    }
    Fallback Off
}