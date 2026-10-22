// 꺾임 모서리에 얹는 검은 띠 전용 셰이더입니다.
// 조명을 전혀 받지 않습니다. 선으로 형태를 전달하는 것이 목적이라
// 빛에 따라 선이 흐려지거나 사라지면 안 됩니다.
//
// 이 방식의 이점: 선은 지오메트리라 조명이 돌아도 그 자리에 있습니다.
// 음영으로 형태를 만들면 빛이 바뀔 때마다 형태가 흔들리는데, 그 문제가 없습니다.
Shader "FBX2NPR/Line"
{
    Properties
    {
        _Color  ("선 색", Color) = (0.02, 0.02, 0.03, 1)
        _Offset ("깊이 밀어내기", Range(-2, 0)) = -1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry+1" }

        Pass
        {
            Name "Line"
            // 띠가 표면 바로 위에 놓이므로 z-파이팅이 납니다. 살짝 앞으로 당깁니다.
            Offset [_Offset], [_Offset]
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float  _Offset;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                return o;
            }

            half4 frag (Varyings i) : SV_Target { return half4(_Color.rgb, 1); }
            ENDHLSL
        }
    }
    Fallback Off
}