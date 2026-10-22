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
        // 빛을 잘 받는 곳은 원본 노멀(코·뺨의 형태감), 어두워지는 곳은 정리된 노멀(얼룩 제거). UV3 에 심은 원본 노멀을 쓴다.
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
        _NoseStop    ("코 음영 임계값 (원본 노멀 수평 ndotl)", Float) = 0.30
        _NoseDebug   ("코(빨강)·눈(파랑) 영역 표시 (디버그)", Range(0,1)) = 0
        // 코 반달 (54번): 코가 평평한 캐릭터는 원본 노멀 대신 코끝 기준 얼굴 평면에 반달(곧은 변 + 반타원 호)을 그린다.
        _NoseMoon      ("코 반달 켜기 (평평한 코)", Range(0,1)) = 0
        _NoseMoonShape ("반달: x 코 중심선에서 곧은 변까지, y 중심 높이(코끝 기준), z 폭, w 반높이 (미터)", Vector) = (0.001, 0.005, 0.005, 0.008)
        _NoseMoonParam ("코 도형: x 기준 빛의 옆 성분, y 깊이 한계 (미터), z 초승달 두께 (0 없음 ~ 1 반달), w 모양 (0 초승달, 1 세모, 2 둥근 사다리꼴, 3 물방울)", Vector) = (0.57, 0.02, 0.5, 0)
        _NoseMoonExtra ("초승달: x 가장 두꺼운 곳 위치 (-0.9 아래 ~ 0.9 위, 0 가운데)", Vector) = (0, 0, 0, 0)
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
        // 고개를 돌리면 코 음영과 외곽선이 어긋나므로, 변환 도구는 원본 노멀을 탄젠트 자리에 실어 둡니다.
        // 켜면 원본 노멀을 UV3 대신 탄젠트에서 읽습니다. 이때 노멀맵은 쓸 수 없습니다(_NormalStrength 0).
        [Toggle] _OrigFromTangent ("원본 노멀을 탄젠트에서 읽기 (스킨 메시)", Float) = 0
        // 얼굴과 몸이 한 메시·한 재질인 캐릭터용 (믹사모: 머리·목·손이 한 메시). 켜면 얼굴 명암선·가장자리 띠·림을
        // 정점의 얼굴 가중치(UV2.x, 변환 도구가 넣음)만큼만 적용하고, 나머지는 일반 밴드 명암으로 둡니다.
        [Toggle] _FaceWeightOn ("얼굴 명암을 얼굴 가중치(UV2.x)만큼만", Float) = 0
        // 머리카락·속눈썹처럼 가닥마다 선이 그어지면 안 되는 메시용.
        // 노멀 버퍼에 카메라 방향을 써서 내부 노멀 엣지를 0 으로 만들고, 실루엣 깊이 엣지만 남깁니다.
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // Core.hlsl 은 Color.hlsl 을 끌고 오지 않습니다. LinearToSRGB 를 쓰려면 직접 넣어야 합니다.
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_Albedo);
            SAMPLER(sampler_Albedo);
            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _LightDir;
                float  _Stop1;
                float  _Stop2;
                float4 _Band0;
                float4 _Band1;
                float4 _Band2;
                float4 _FaceForward;
                float4 _FaceCenter;
                float  _FaceFlat;
                float  _EdgeDark;
                float  _EdgeWidth;
                float  _EdgeTaper;
                float  _EdgeSoft;
                float  _RimLight;
                float  _RimWidth;
                float  _OrigBlend;
                float  _OrigLo;
                float  _OrigHi;
                float  _FaceCurve;
                float4 _FaceUp;
                float  _CurveStop;
                float  _CurveSoft;
                float  _CurveTilt;
                float  _ChinScale;
                float  _ChinBend;
                float  _AnchorOn;
                float  _AnchorFix;
                float4 _AnchorLight;
                float4 _RadialCenter;
                float4 _AnchorCA;
                float4 _AnchorCB;
                float4 _AnchorJA;
                float4 _AnchorJB;
                float  _AnchorTopSlope;
                float  _AnchorGain;
                float  _AnchorSoft;
                float  _NoseShade;
                float4 _NoseTip;
                float4 _NoseRadius;
                float  _NoseStop;
                float  _NoseDebug;
                float  _NoseMoon;
                float4 _NoseMoonShape;
                float4 _NoseMoonParam;
                float4 _NoseMoonExtra;
                float4 _NosePolyA;
                float4 _NosePolyB;
                float4 _NosePolyParam;
                float  _EyeAvoid;
                float4 _EyeA;
                float4 _EyeB;
                float4 _EyeRadius;
                float  _NormalStrength;
                float  _ShowNormal;
                float  _UseOrigNormal;
                float  _AlbedoOn;
                float  _Posterize;
                float  _PosterizeLum;
                float  _Saturation;
                float  _Lift;
                float  _Flatten;
                float  _FlattenMip;
                float  _Cutout;
                float  _Cutoff;
                float  _OrigFromTangent;
                float  _FaceWeightOn;
                float  _EmissionOn;
                float4 _EmissionColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 faceW      : TEXCOORD2;   // x = 얼굴 가중치 (변환 도구가 넣음, 없으면 0)
                float3 origOS     : TEXCOORD3;   // 임포트 때 심어 둔 원본 노멀
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

            // 수평 ndotl: 위 방향 성분을 빼고 방향만 비교한다.
            // 수평 성분이 짧은 노멀(턱 밑·정수리)은 정규화가 불안정하므로 0 쪽으로 줄인다 (그 부위는 그림자 쪽으로 간다).
            // 사각형까지의 부호 있는 거리 (안쪽이 음수). 변까지 거리의 최소값 + 교차 횟수로 안팎을 가린다 (Inigo Quilez 의 sdPolygon).
            // 물방울 거리 (안쪽이 음수): 반지름 ra 인 원(pa)과 rb 인 원(pb)을 접선으로 이은 모양 (Inigo Quilez 의 sdUnevenCapsule).
            // rb 를 작게 두면 pb 쪽이 뾰족한 물방울이 된다.
            float SdDrop(float2 p, float2 pa, float2 pb, float ra, float rb)
            {
                p -= pa; pb -= pa;
                float h = max(dot(pb, pb), 1e-12);
                float2 q = float2(dot(p, float2(pb.y, -pb.x)), dot(p, pb)) / h;
                q.x = abs(q.x);
                float b = ra - rb;
                float2 c = float2(sqrt(max(h - b * b, 1e-12)), b);
                float k = c.x * q.y - c.y * q.x;
                float m = dot(c, q), n = dot(q, q);
                if (k < 0.0) return sqrt(h * n) - ra;
                if (k > c.x) return sqrt(h * (n + 1.0 - 2.0 * q.y)) - rb;
                return m - ra;
            }

            float SdQuad(float2 p, float2 v0, float2 v1, float2 v2, float2 v3)
            {
                float2 v[4] = { v0, v1, v2, v3 };
                float d = dot(p - v[0], p - v[0]);
                float s = 1.0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float2 vi = v[k], vj = v[(k + 3) % 4];
                    float2 e = vj - vi, w = p - vi;
                    float2 b = w - e * saturate(dot(w, e) / max(dot(e, e), 1e-12));
                    d = min(d, dot(b, b));
                    bool c0 = p.y >= vi.y, c1 = p.y < vj.y, c2 = e.x * w.y > e.y * w.x;
                    if ((c0 && c1 && c2) || (!c0 && !c1 && !c2)) s = -s;
                }
                return s * sqrt(d);
            }

            float HorizNdotL(float3 nrm, float3 lhN, float3 up)
            {
                float3 h = nrm - up * dot(nrm, up);
                return dot(h, lhN) / max(length(h), 0.35);
            }

            // 눈 타원 마스크 (얼굴 좌우·위 평면에서 잰 타원, 눈보다 한참 뒤쪽은 제외). 가장자리는 부드럽게 줄어든다.
            // 빛 쪽으로는 타원을 끝까지 늘인다. 타원 그대로 쓰면 그림자 안에 눈만 밝은 섬이 생겼다.
            // 늘이면 밝은 쪽과 눈 사이가 이어져, 명암선이 한 줄로 눈 바깥을 돌아 나간다 (레퍼런스의 모양).
            float EyeMask(float3 p, float3 c, float3 right, float3 up, float3 fwd, float lightSide)
            {
                float3 rel = p - c;
                float rx = dot(rel, right);
                float ex = (rx * lightSide > 0.0) ? 0.0 : rx / max(_EyeRadius.x, 1e-5);
                float ey = dot(rel, up) / max(_EyeRadius.y, 1e-5);
                float front = step(-_EyeRadius.z, dot(rel, fwd));
                // 안쪽 0.25 까지는 꽉 차고 바깥으로 천천히 준다. 0.45 에서 시작하면 효과가 가장자리에서 한꺼번에 빠져
                // 튀어나온 부분의 위아래가 수평으로 꺾였다(Ch07). 천천히 빠지면 선이 곡선으로 돌아 나온다.
                return (1.0 - smoothstep(0.25, 1.0, sqrt(ex * ex + ey * ey))) * front;
            }

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.origWS     = TransformObjectToWorldNormal(_OrigFromTangent > 0.5 ? v.tangentOS.xyz : v.origOS);
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

                // _OrigFromTangent 일 때는 탄젠트 자리에 원본 노멀이 들어 있어 TBN 을 만들 수 없습니다 (노멀맵 꺼짐).
                if (_NormalStrength > 0.0001 && _OrigFromTangent < 0.5)
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
                        // 눈 피하기: 눈 타원 안에서는 임계값을 낮춰 밝게 둔다. 마스크가 부드럽게 줄어드니
                        // 명암선이 타원 가장자리를 따라 눈 바깥으로 휘어 돌고, 눈 위아래에서 원래 선으로 이어진다.
                        float stopC = _CurveStop;
                        // 지수함수 꼴 명암선: 턱 끝(t=0)에서 임계값을 가장 크게 올려 선을 턱 끝까지 끌어오고,
                        // 위로 갈수록 exp(-t) 로 빠르게 사라져 옆면에서는 세로 한 줄로 돌아간다. 턱 끝 아래(목)는 계속 어둡다.
                        if (_CurveTilt > 0.0001)
                        {
                            float hC = dot(i.positionWS - _NoseTip.xyz, up) + _ChinScale;           // 턱 끝 기준 높이 (m)
                            // 지수는 8 에서 자른다: 몸통처럼 턱보다 한참 아래 정점에서 exp 가 무한대가 되면 smoothstep 이 NaN 을 낸다.
                            stopC += _CurveTilt * exp(min(-hC / max(_ChinScale * _ChinBend, 1e-4), 8.0));
                        }
                        if (_EyeAvoid > 0.0001 || _NoseDebug > 0.5)
                        {
                            float3 fwdE   = normalize(_FaceForward.xyz);
                            float3 rightE = normalize(cross(up, fwdE));
                            float  sideL  = sign(dot(lhN, rightE));   // 빛이 얼굴 오른쪽(+1)에서 오나 왼쪽(-1)에서 오나
                            float  mE     = max(EyeMask(i.positionWS, _EyeA.xyz, rightE, up, fwdE, sideL), EyeMask(i.positionWS, _EyeB.xyz, rightE, up, fwdE, sideL));
                            // 1.5 로 크게 낮추면 가장자리 조금만 걸쳐도 끝까지 밝아져 경계가 타원 모양 그대로 각이 졌다.
                            // 0.9 로 두면 선이 원래 자리와 타원 사이를 매끄럽게 이어 간다.
                            stopC -= mE * _EyeAvoid * 0.9;
                            eyeDbg = mE;
                        }
                        // 50번 기준점 명암선: 노멀이 아니라 얼굴 위의 위치로 선을 긋는다 (위의 위치·당김·눈 피하기를 모두 대신한다).
                        // 처음에는 기준점에서 잰 노멀 임계값으로 그었는데, 유니티짱은 방사형 노멀 중심이 얼굴 표면 바로 뒤에 있어
                        // 노멀이 옆으로 크게 퍼지는 바람에 얼굴 그림자가 통째로 사라졌다. 위치로 그으면 모든 캐릭터에서
                        // 눈꼬리·턱선 기준점을 정확히 지난다 (서브컬처 게임이 얼굴 그림자를 그림으로 지정하는 것과 같은 원리).
                        float litA = -1.0;
                        if (_AnchorOn > 0.5)
                        {
                            float3 fwdA   = normalize(_FaceForward.xyz);
                            float3 rightA = normalize(cross(up, fwdA));
                            float  sideA  = (dot(lhN, rightA) >= 0.0) ? -1.0 : 1.0;          // 그림자 쪽 = 빛 반대. +1 이면 +X 쪽
                            float3 Cp     = (sideA > 0.0) ? _AnchorCA.xyz : _AnchorCB.xyz;   // 눈꼬리 살짝 바깥
                            float3 Jp     = (sideA > 0.0) ? _AnchorJA.xyz : _AnchorJB.xyz;   // 턱 끝 조금 옆 턱선
                            // 얼굴 평면 좌표: u = 그림자 쪽으로 얼마나 바깥인가, v = 높이
                            float uC = dot(Cp - _RadialCenter.xyz, rightA) * sideA;
                            float uJ = dot(Jp - _RadialCenter.xyz, rightA) * sideA;
                            float uP = dot(i.positionWS - _RadialCenter.xyz, rightA) * sideA;
                            float vC = dot(Cp, up), vJ = dot(Jp, up), vP = dot(i.positionWS, up);
                            float uB;
                            if (vP >= vC)
                            {
                                // 눈꼬리 위: 끊기지 않고 거의 세로로 위까지 이어진다 (살짝 바깥으로).
                                // 한때 여기서 선을 닫았더니(그 위는 밝게) 눈꼬리 높이에서 그림자가 수평으로 뚝 잘렸다.
                                uB = uC + (vP - vC) * _AnchorTopSlope;
                            }
                            else
                            {
                                // 눈꼬리 아래: 볼을 타고 내려가다 턱 가까이에서 지수 꼴로 휘어 턱선 점을 지난다.
                                // 정규화해서 눈꼬리 높이에서 정확히 uC, 턱선 점 높이에서 정확히 uJ.
                                float lam = max((vC - vJ) * _ChinBend, 1e-4);
                                float eC  = exp(-(vC - vJ) / lam);
                                float eP  = exp(min((vJ - vP) / lam, 8.0));
                                uB = uC + (uJ - uC) * (eP - eC) / max(1.0 - eC, 1e-4);
                            }
                            // 빛을 따라 움직이는 모드: 빛이 옆으로 갈수록 그림자가 커져 선이 안쪽으로, 정면 쪽이면 바깥으로 간다.
                            // 기준 빛일 때 이동량이 0 이라 선이 기준점을 지난다. 고정 모드는 늘 기준점을 지난다(좌우만 바뀜).
                            if (_AnchorFix < 0.5)
                            {
                                float3 ld  = normalize(_AnchorLight.xyz);
                                float3 ldh = ld - up * dot(ld, up);
                                ldh /= max(length(ldh), 1e-4);
                                uB -= (abs(dot(lhN, rightA)) - abs(dot(ldh, rightA))) * _AnchorGain;
                            }
                            litA = smoothstep(-_AnchorSoft, _AnchorSoft, uB - uP);   // 선보다 빛 쪽이면 밝음
                            anchorDbg = step(min(length(i.positionWS - Cp), length(i.positionWS - Jp)), 0.003);
                        }
                        lit = (litA >= 0.0) ? litA : smoothstep(stopC - _CurveSoft, stopC + _CurveSoft, HorizNdotL(n, lhN, up));
                        // 54번 코 반달 → 55번 초승달: 코가 평평한 캐릭터(유니티짱)는 원본 노멀 그림자가 큰 메시 면을 따라 꺾여 사다리꼴이 됐다.
                        // 그 자리·크기를 임포트 때 재 두고(FaceLandmarks.FitNoseMoon), 얼굴 평면에 초승달을 그린다.
                        // 구(球)에 옆빛이 들 때의 그림자처럼 바깥 호(윤곽)와 안쪽 호(명암 경계)가 위아래 끝에서 만난다.
                        // 54번은 안쪽이 곧은 변인 반달이었는데 "너무 딱 반달이라 어색"했다.
                        // 빛 반대쪽에 그리고, 빛이 반대편에서 오면 거울처럼 넘어간다. 바깥 호는 고정이고
                        // 안쪽 호만 빛을 따라 움직인다: 두께 ∝ (1 - 빛의 정면 성분) — 구의 명암 경계와 같은 비율.
                        // 기준 빛에서 두께 = _NoseMoonParam.z, 빛이 정면이면 사라지고 옆으로 갈수록 반달(두께 1)까지 두꺼워진다.
                        if (_NoseMoon > 0.5)
                        {
                            float3 fwdM   = normalize(_FaceForward.xyz);
                            float3 rightM = normalize(cross(up, fwdM));
                            float3 relM   = i.positionWS - _NoseTip.xyz;
                            float  sideM  = dot(lhN, rightM);                       // 빛이 +right 쪽에서 오면 양수
                            float  sgnM   = (sideM >= 0.0) ? -1.0 : 1.0;            // 그림자 쪽 부호
                            float  cosR   = sqrt(saturate(1.0 - _NoseMoonParam.x * _NoseMoonParam.x));   // 기준 빛의 정면 성분
                            float  fL     = (1.0 - dot(lhN, fwdM)) / max(1.0 - cosR, 1e-3);   // 기준 빛에서 1, 정면 빛 0, 옆으로 갈수록 커짐
                            float  aM     = max(_NoseMoonShape.z, 1e-5);             // 가로 폭 (초승달은 바깥 호의 가로 반지름)
                            float  bM     = max(_NoseMoonShape.w, 1e-5);             // 세로 반높이
                            float  uM     = dot(relM, rightM) * sgnM - _NoseMoonShape.x;   // 콧선 쪽 기준선에서 그림자 쪽으로 (m)
                            float  vM     = dot(relM, up) - _NoseMoonShape.y;
                            float  dM;
                            if (_NoseMoonParam.w > 2.5)
                            {
                                // 61번 물방울: 사용자 레퍼런스(애니 일러스트)의 코 오른쪽 그림자. 위는 뾰족(콧대 쪽), 아래는 둥글고 통통(코끝 옆),
                                // 왼쪽 변은 콧대 선에 붙는다. _NosePolyA = (아래 원 중심, 위 끝 원 중심), _NosePolyParam = (아래 반지름, 위 반지름, 콧대 선 위치).
                                // 콧대 선 쪽은 두고 빛이 옆으로 갈수록 뺨 쪽으로 넓어진다(최대 1.5배).
                                float2 pD = float2(dot(relM, rightM) * sgnM, dot(relM, up));
                                float  sD = clamp(fL, 1e-3, 1.5);
                                pD.x = _NosePolyParam.z + (pD.x - _NosePolyParam.z) / sD;
                                dM = -SdDrop(pD, _NosePolyA.xy, _NosePolyA.zw, _NosePolyParam.x, _NosePolyParam.y);
                            }
                            else if (_NoseMoonParam.w > 1.5)
                            {
                                // 57번 둥근 사다리꼴: 사용자가 결국 원래 원본 노멀 그림자(53번)의 사다리꼴 느낌이 가장 자연스럽다고 했다.
                                // 그 윤곽을 사각형으로 줄이고 꼭짓점을 반지름만큼 둥글게 (꼭짓점은 CPU 에서 미리 안쪽으로 줄여 변 자리를 지킨다).
                                // 바깥 끝은 두고, 빛이 옆으로 갈수록 콧선 쪽으로 넓어진다(원본 노멀 그림자가 커지는 방향, 최대 1.5배).
                                float2 pQ = float2(dot(relM, rightM) * sgnM, dot(relM, up));
                                float  sQ = clamp(fL, 1e-3, 1.5);
                                pQ.x = _NosePolyParam.y - (_NosePolyParam.y - pQ.x) / sQ;
                                dM = -(SdQuad(pQ, _NosePolyA.xy, _NosePolyA.zw, _NosePolyB.xy, _NosePolyB.zw) - _NosePolyParam.x);
                            }
                            else if (_NoseMoonParam.w > 0.5)
                            {
                                // 56번 세모: 콧선 쪽 세로 변 + 코끝 높이의 밑변 + 빗변, 꼭짓점은 위(콧대 쪽). 애니메이션 코 옆 그림자의 흔한 모양.
                                // 밑변 폭만 빛을 따라 변한다(초승달 두께와 같은 비율, 최대 1.5배).
                                float wT   = aM * min(fL, 1.5);
                                float dHyp = (wT * (bM - vM) - 2.0 * bM * uM) / sqrt(wT * wT + 4.0 * bM * bM);   // 빗변 안쪽이 양수 (거리)
                                dM = min(min(uM, vM + bM), dHyp);
                            }
                            else
                            {
                                float thick = saturate(_NoseMoonParam.z * fL);
                                float qv    = clamp(vM / bM, -1.0, 1.0);
                                // 59번: 가장 두꺼운 곳을 sB 로 옮긴다(끝점 ±1 은 그대로). 음수면 코끝 쪽이 두껍고 콧대 쪽으로 가늘게 빠진다.
                                float sB    = clamp(_NoseMoonExtra.x, -0.9, 0.9);
                                float qw    = (qv - sB) / (1.0 - sB * qv);
                                float prof  = sqrt(saturate(1.0 - qw * qw));          // 높이별 호의 가로 비율 (끝에서 0)
                                float dOut  = aM * prof - uM;                         // 바깥 호 안쪽이 양수
                                float dIn   = uM - aM * (1.0 - thick) * prof;         // 안쪽 호 바깥이 양수
                                // 위아래 끝 밖은 확실히 바깥으로: 없으면 끝점을 잇는 세로선 위아래로 반투명 실선이 남을 수 있다
                                dM = (thick > 1e-3) ? min(min(dOut, dIn), bM - abs(vM)) : -1.0;
                            }
                            float  eM     = max(fwidth(dM) * 0.75, 1e-6);              // 경계 1~2 픽셀 (53번과 같은 이유)
                            float  shM    = smoothstep(-eM, eM, dM) * step(-_NoseMoonParam.y, dot(relM, fwdM)) * step(1e-3, fL);
                            lit = min(lit, 1.0 - shM * _NoseShade);
                            noseDbg = shM;
                        }
                        // 코: 코끝 주변 타원 안에서만 원본 노멀로 그림자를 넣는다. 같은 임계값 방식이라 모양이 단순하다.
                        else if (_NoseShade > 0.0001 || _NoseDebug > 0.5)
                        {
                            float3 fwd   = normalize(_FaceForward.xyz);
                            float3 right = normalize(cross(up, fwd));
                            float3 rel   = i.positionWS - _NoseTip.xyz;
                            float  ry    = dot(rel, up);
                            float  nx    = dot(rel, right) / max(_NoseRadius.x, 1e-5);
                            float  ny    = ry / max(ry > 0.0 ? _NoseRadius.y : _NoseRadius.z, 1e-5);
                            float  front = step(-_NoseRadius.w, dot(rel, fwd));
                            float  mN    = (1.0 - smoothstep(0.55, 1.0, sqrt(nx * nx + ny * ny))) * front;
                            float  sN    = HorizNdotL(normalize(i.origWS), lhN, up);
                            // 영역 가장자리에서 그림자를 흐리게 하면 뿌옇게 번진다. 대신 임계값을 낮춰 그림자 자체가
                            // 가늘어지다 사라지게 한다: 경계는 끝까지 선명하고 끝이 뾰족해진다 (mN=0 이면 임계값 -1.7 로 그림자 없음).
                            float  stopN = _NoseStop - (1.0 - mN) * 2.0;
                            // 52번: 경계 폭을 화면에서 1~2 픽셀 이상으로 둔다. 유니티짱처럼 작은 코는 한 픽셀 사이에 sN 이 크게 바뀌어
                            // 고정 폭(_CurveSoft)으로는 경계가 픽셀보다 얇아져 계단이 졌다.
                            float  eN    = max(_CurveSoft, fwidth(sN - stopN) * 0.75);
                            float  shN   = 1.0 - smoothstep(stopN - eN, stopN + eN, sN);
                            lit = min(lit, 1.0 - shN * _NoseShade);
                            noseDbg = mN;
                        }
                    }
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
        // 정확히 그 자리에 선으로 돌아옵니다. 원본 노멀은 임포트 때 UV3 에 실어 둡니다.
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

            CBUFFER_START(UnityPerMaterial)
                float4 _LightDir;
                float  _Stop1;
                float  _Stop2;
                float4 _Band0;
                float4 _Band1;
                float4 _Band2;
                float  _NormalStrength;
                float  _ShowNormal;
                float  _UseOrigNormal;
                float  _OutlineFlatNormal;
                float  _Cutout;
                float  _Cutoff;
                float  _OrigFromTangent;
                float4 _InnerLineFade;
                float  _FaceWeightOn;
            CBUFFER_END

            struct dnAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;     // _OrigFromTangent 일 때 원본 노멀이 여기 들어 있다 (스킨 메시)
                float3 origOS     : TEXCOORD3;   // 임포트 때 심어 둔 원본 노멀
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
                float3 orig = (_OrigFromTangent > 0.5) ? v.tangentOS.xyz : v.origOS;
                float3 src = (_UseOrigNormal > 0.5) ? orig : v.normalOS;
                // UV3 가 비어 있으면 (0,0,0) 이므로 그때는 메시 노멀로 되돌립니다.
                if (dot(src, src) < 1e-8) src = v.normalOS;
                if (_OutlineFlatNormal > 0.5)
                {
                    // 정점마다 카메라를 향하는 벡터. 메시 안에서 매끄럽게 변하므로 소벨이 잡을 노멀 엣지가 없습니다.
                    float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                    o.normalWS = normalize(_WorldSpaceCameraPos - pw);
                    return o;
                }
                o.normalWS = TransformObjectToWorldNormal(src);
                if (_InnerLineFade.y > 1e-7)
                {
                    float3 pw = TransformObjectToWorld(v.positionOS.xyz);
                    float  dv = abs(TransformWorldToView(pw).z);
                    // 픽셀 하나가 덮는 실제 크기 (미터). 원근: 2 d / (P11 x 화면 높이), 직교: 2 x 반높이 / 화면 높이
                    float  fp = (unity_OrthoParams.w > 0.5) ? (2.0 * unity_OrthoParams.y / _ScreenParams.y)
                                                            : (2.0 * dv / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y));
                    float  t  = (_InnerLineFade.y > _InnerLineFade.x) ? smoothstep(_InnerLineFade.x, _InnerLineFade.y, fp) : step(_InnerLineFade.y, fp);
                    // 얼굴 재질을 받는 피부 메시만 지운다(리그가 _InnerLineFade 를 얼굴 재질 칸에만 넣으므로 옷 메시는 이 블록에 오지 않는다).
                    // 얼굴(x)은 원래 거리에서, 나머지 피부(y: 목·손·발목 등)는 얼굴의 두 배 거리에서부터 지운다 — 얼굴만 지우면 게임 거리에서
                    // 작게 보이는 피부가 선 색 덩어리로 남았고(Ch07 툰, 2026-10-14), 같은 거리로 지우면 클로즈업에서 턱선(얼굴과 목의 노멀 차)이 흐려졌다.
                    // y 가 없는 예전 메시는 0 이라 x 만 쓴 것과 같고, 얼굴 정점(x=y=1)은 두 배 거리의 비중이 늘 더 작아 예전과 같다.
                    if (_FaceWeightOn > 0.5)
                    {
                        float2 fn = _InnerLineFade.xy * 2.0;
                        float  tn = (fn.y > fn.x) ? smoothstep(fn.x, fn.y, fp) : step(fn.y, fp);
                        t = max(t * saturate(v.faceW.x), tn * saturate(v.faceW.y));
                    }
                    o.normalWS = normalize(lerp(o.normalWS, normalize(_WorldSpaceCameraPos - pw), t));
                }
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

            CBUFFER_START(UnityPerMaterial)
                float  _Cutout;
                float  _Cutoff;
            CBUFFER_END

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

            CBUFFER_START(UnityPerMaterial)
                float  _Cutout;
                float  _Cutoff;
            CBUFFER_END

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