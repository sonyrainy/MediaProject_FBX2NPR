// 얼굴 규칙을 UV 공간에 굽는 패스 (FaceSdfBake). 화면 대신 텍스처 칸에 그려서, 빛 각도 하나의 명암을 흑백 마스크로 얻습니다.
// 규칙은 화면 패스와 같은 파일(ToonFaceRule.hlsl)을 씁니다. 그래서 구운 초안이 화면과 어긋날 곳이 줄어듭니다.
// 재질 값은 리그가 넣습니다. 그래서 Properties 를 두지 않습니다.
Shader "Hidden/FBX2NPR/FaceMaskBake"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "OriginalNormals.hlsl"
            #include "ToonBandInput.hlsl"
            #define FACE_MASK_BAKE
            #include "ToonFaceRule.hlsl"

            float4 _BakeUvRect;   // xy 시작, zw 크기: 이 UV 사각형이 렌더 타깃 전체를 채운다

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float3 origOS     : TEXCOORD3;
                float3 origTS     : TEXCOORD4;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 origWS     : TEXCOORD2;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float2 p = (v.uv - _BakeUvRect.xy) / _BakeUvRect.zw * 2.0 - 1.0;
            #if UNITY_UV_STARTS_AT_TOP
                p.y = -p.y;
            #endif
                o.positionCS = float4(p, 0.5, 1.0);
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.normalWS   = TransformObjectToWorldNormal(v.normalOS);
                float3 origOS = (_OrigTangentSpace > 0.5 && dot(v.origTS, v.origTS) > 0.25) ? DecodeOriginalNormal(v.origTS, v.normalOS, v.tangentOS) : v.origOS;
                o.origWS     = TransformObjectToWorldNormal(origOS);
                return o;
            }

            // R 큰 명암선(밝으면 1), G 코 그림자가 없으면 1, A 칸이 덮였으면 1
            float4 frag (Varyings i) : SV_Target
            {
                float3 up = normalize(_FaceUp.xyz);
                float3 l  = normalize(_LightDir.xyz);
                float3 lh = l - up * dot(l, up);
                float3 lhN = lh / max(length(lh), 1e-4);
                FaceShade s = EvalFaceRule(i.positionWS, normalize(i.normalWS), i.origWS, lhN, up);
                return float4(s.lit, 1.0 - s.noseShade, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
