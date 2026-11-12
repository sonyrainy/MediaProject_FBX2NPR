// 외곽선 피처가 얼굴 렌더러를 한 번 더 그려 원본 노멀을 남기는 셰이더.
//
// 캐릭터 재질이 ToonBand 가 아니어도(UTS, lilToon, URP Lit …) 코·입 선이 살아 있게 하려고 만들었습니다.
// 다른 셰이더의 노멀 버퍼에는 정리된 노멀이 들어가 코·입의 굴곡이 지워져 있기 때문입니다.
// 피처가 렌더러의 재질 대신 이 셰이더로 그리되(override shader) 재질 값은 그대로 받으므로,
// ToonBand 재질이면 안쪽 선 지우기·실루엣만·컷아웃 값까지 DepthNormals 패스와 같은 결과가 나옵니다. 다른 재질이면 그 값들은 꺼진 기본값이고,
// 실루엣만은 변환 도구가 렌더러의 렌더링 레이어에도 켜 두어 재질과 상관없이 지킵니다 (LineNormal.hlsl 의 FlatLine). 코 둘레 값은 리그가 렌더러마다 넣습니다.
// 출력: xyz 외곽선용 노멀(월드), w 카메라까지 거리. 소벨 셰이더가 장면 깊이와 같은 자리에서만 이 노멀을 씁니다.
// 실루엣 선만 쓰는 얼굴(재질 _OutlineFlatNormal 이나 렌더러 표시)의 안쪽(얼굴 가중치 0.9 초과)은 w 를 음수로 남겨, 소벨 셰이더가 거기서 깊이가 끊긴 곳만 선으로 보게 합니다.
// 얼굴 가중치로 자르는 것은 같은 재질을 쓰는 몸의 선까지 바꾸지 않으려는 것입니다.
Shader "FBX2NPR/LineNormals"
{
    Properties
    {
        [NoScaleOffset] _Albedo ("알베도 (컷아웃용)", 2D) = "white" {}
        _OutlineFlatNormal ("외곽선용 노멀을 카메라 방향으로", Float) = 0
        _InnerLineFade ("안쪽 선을 멀어지면 지우기", Vector) = (0, 0, 0, 0)
        _FaceWeightOn ("얼굴 가중치(UV2) 쓰기", Float) = 0
        _Cutout ("알파 컷아웃", Float) = 0
        _Cutoff ("컷아웃 문턱", Float) = 0.5
        // 코 둘레 안쪽 선 지우기. 얼굴 리그가 렌더러마다 넣는다 (재질이 ToonBand 가 아니어도 같은 값).
        _FaceUp ("얼굴 위 방향 (월드)", Vector) = (0, 1, 0, 0)
        _FaceForward ("얼굴 정면 방향 (월드)", Vector) = (0, 0, 1, 0)
        _NoseTip ("코끝 위치 (월드)", Vector) = (0, 0, 0, 0)
        _NoseRadius ("코 영역 반경 (x 좌우, y 위, z 아래, w 뒤쪽 깊이)", Vector) = (0.02, 0.045, 0.012, 0.04)
        _NoseLineFade ("코 영역 안쪽 선 지우기", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        Pass
        {
            Name "LineNormals"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_Albedo);
            SAMPLER(sampler_Albedo);

            CBUFFER_START(UnityPerMaterial)
                float  _OutlineFlatNormal;
                float4 _InnerLineFade;
                float  _FaceWeightOn;
                float  _Cutout;
                float  _Cutoff;
                float4 _FaceUp;
                float4 _FaceForward;
                float4 _NoseTip;
                float4 _NoseRadius;
                float  _NoseLineFade;
            CBUFFER_END

            #include "OriginalNormals.hlsl"
            #include "LineNormal.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 faceW      : TEXCOORD2;   // x = 얼굴 가중치, y = 피부 메시 전체 (변환 도구가 넣음)
                float3 origTS     : TEXCOORD4;   // 탄젠트 공간 원본 노멀 (변환 도구가 넣음, 없으면 0)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float2 uv         : TEXCOORD1;
                float  eyeDepth   : TEXCOORD2;
                float  faceW      : TEXCOORD3;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                o.eyeDepth = -TransformWorldToView(TransformObjectToWorld(v.positionOS.xyz)).z;
                // UV4 가 비어 있으면(변환하지 않은 메시) 메시 노멀을 씁니다.
                float3 src = dot(v.origTS, v.origTS) > 0.25 ? DecodeOriginalNormal(v.origTS, v.normalOS, v.tangentOS) : v.normalOS;
                o.normalWS = LineNormalWS(v.positionOS.xyz, src, v.faceW);
                o.faceW = v.faceW.x;
                return o;
            }

            float4 frag (Varyings i) : SV_Target
            {
                if (_Cutout > 0.5) clip(SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, i.uv).a - _Cutoff);
                bool flatInner = FlatLine() && i.faceW > 0.9;
                return float4(normalize(i.normalWS), flatInner ? -i.eyeDepth : i.eyeDepth);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
