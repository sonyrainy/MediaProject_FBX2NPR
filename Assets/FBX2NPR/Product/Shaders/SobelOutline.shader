// 화면 공간 외곽선. 깊이와 노멀 버퍼에 소벨 필터를 걸어 경계를 찾습니다.
//
// 메시 띠 방식을 버리고 이쪽으로 온 이유는 두 가지가 구조적으로 해결되기 때문입니다.
//   1) 두께가 화면 기준이라 카메라 거리와 각도에 관계없이 일정합니다.
//      메시 띠는 월드 공간이라 멀어지면 가늘어지고 비스듬히 보면 찌그러졌습니다.
//   2) 결과를 원래 색에 곱하므로 선이 항상 배경보다 어둡습니다.
//      메시 띠는 고정색이라 그늘진 피부 위에서 오히려 밝게 보여 긁힌 자국처럼 읽혔습니다.
Shader "FBX2NPR/SobelOutline"
{
    Properties
    {
        _Darkness   ("선 어둡기", Range(0,1)) = 0.35
        _Thickness  ("선 두께 (픽셀)", Range(0.5,4)) = 1.2
        _DepthScale ("깊이 민감도", Range(0,40)) = 12
        _NormalScale("노멀 민감도", Range(0,8)) = 2.2
        _Threshold  ("문턱값", Range(0,1)) = 0.12
        _FlatFaceJumpOnly ("실루엣만 얼굴 안은 깊이 끊김만 선으로 (0/1)", Float) = 1
        // 0=정상, 1=엣지만, 2=씬 노멀, 3=깊이, 4=실루엣만 얼굴 안. 버퍼가 비었는지 눈으로 확인할 때 씁니다.
        _Debug      ("디버그 출력", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "SobelOutline"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag
            // Core.hlsl 이 먼저 와야 합니다. Blit.hlsl 은 TEXTURE2D_X 등을 이미 정의된 것으로 보고 씁니다.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            float _Darkness, _Thickness, _DepthScale, _NormalScale, _Threshold, _Debug, _FlatFaceJumpOnly;

            // 외곽선 피처가 얼굴 렌더러를 원본 노멀로 다시 그린 텍스처 (xyz 노멀, w 카메라까지 거리, 없는 곳은 0).
            // 재질 셰이더가 노멀 버퍼에 정리된 노멀을 써도(UTS 등) 코·입 선이 살아 있게 합니다.
            // 실루엣 선만 쓰는 얼굴의 안쪽(얼굴 가중치가 높은 곳)은 w 가 음수입니다 (LineNormals.shader).
            TEXTURE2D_X(_FBX2NPRLineNormals);
            float _LineNormalsOn;

            // 3x3 소벨 커널의 가로/세로 가중치
            static const float2 kOff[8] = {
                float2(-1,-1), float2(0,-1), float2(1,-1),
                float2(-1, 0),               float2(1, 0),
                float2(-1, 1), float2(0, 1), float2(1, 1) };
            static const float kGx[8] = { -1, 0, 1, -2, 2, -1, 0, 1 };
            static const float kGy[8] = { -1,-2,-1,  0, 0,  1, 2, 1 };

            float EyeDepth(float2 uv)
            {
                return LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            }

            // 장면 노멀. 원본 노멀로 다시 그린 얼굴이 같은 깊이에 있으면 그 노멀을 씁니다 (앞에 다른 물체가 가리면 장면 노멀 그대로).
            float3 Normals(float2 uv, float eyeDepth)
            {
                float3 n = SampleSceneNormals(uv);
                if (_LineNormalsOn > 0.5)
                {
                    float4 ln = SAMPLE_TEXTURE2D_X(_FBX2NPRLineNormals, sampler_PointClamp, uv);
                    if (ln.w != 0.0 && abs(abs(ln.w) - eyeDepth) < 1e-3 * eyeDepth + 1e-4) n = ln.xyz;
                }
                return n;
            }

            // 실루엣 선만 쓰는 얼굴이 이 자리에 보이는가 (외곽선 피처가 w 를 음수로 남긴 화소, 앞에 다른 물체가 가리면 아님).
            bool FlatFace(float2 uv, float eyeDepth)
            {
                if (_LineNormalsOn < 0.5) return false;
                float4 ln = SAMPLE_TEXTURE2D_X(_FBX2NPRLineNormals, sampler_PointClamp, uv);
                return ln.w < 0.0 && abs(-ln.w - eyeDepth) < 1e-3 * eyeDepth + 1e-4;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float2 uv = i.texcoord;
                float2 texel = (1.0 / _ScreenParams.xy) * _Thickness;

                float dC = EyeDepth(uv);
                float3 nC = Normals(uv, dC);
                bool flatAll = FlatFace(uv, dC);

                float dgx = 0, dgy = 0;
                float3 ngx = 0, ngy = 0;
                float dN[8];
                [unroll]
                for (int k = 0; k < 8; k++)
                {
                    float2 o = uv + kOff[k] * texel;
                    float d = EyeDepth(o);
                    float3 n = Normals(o, d);
                    dN[k] = d;
                    dgx += d * kGx[k];  dgy += d * kGy[k];
                    ngx += n * kGx[k];  ngy += n * kGy[k];
                    flatAll = flatAll && FlatFace(o, d);
                }

                float dGrad = sqrt(dgx*dgx + dgy*dgy);
                // 실루엣 선만 쓰는 얼굴 안(3x3 이 모두 그 얼굴)에서는 깊이가 끊긴 만큼만 선으로 봅니다.
                // 마주 보는 두 이웃과 중심의 2차 차분은 고르게 기운 면에서 0, 앞뒤로 떨어진 곳에서 끊긴 크기라서
                // 코볼·입 옆처럼 비스듬히 돌아가는 면(흐린 겹선이 팔자주름처럼 잡혔다, Night Dress)은 빠지고 턱 아래 목처럼 떨어진 곳은 남습니다.
                // 4배는 소벨과 눈금을 맞추는 값입니다 (끊김 J 에서 소벨 4J, 2차 차분 J).
                // 처음엔 이 얼굴 안의 깊이 민감도를 0.25배로 낮췄는데, 목까지 얼굴 가중치가 높은 모델(CasualOutfit)에서 턱선이 흐려졌다 (2026-11-03).
                if (flatAll && _FlatFaceJumpOnly > 0.5)
                {
                    float jump = max(max(abs(dN[3] + dN[4] - 2.0 * dC), abs(dN[1] + dN[6] - 2.0 * dC)),
                                     max(abs(dN[0] + dN[7] - 2.0 * dC), abs(dN[2] + dN[5] - 2.0 * dC)));
                    dGrad = min(dGrad, 4.0 * jump);
                }
                // 깊이는 거리에 비례해 커지므로 중심 깊이로 나눠 거리와 무관하게 만듭니다.
                float dEdge = dGrad / max(dC, 1e-4) * _DepthScale;
                float nEdge = sqrt(dot(ngx,ngx) + dot(ngy,ngy)) * _NormalScale;

                float edge = max(dEdge, nEdge);
                edge = saturate((edge - _Threshold) / max(1.0 - _Threshold, 1e-4));

                if (_Debug > 0.5)
                {
                    if (_Debug < 1.5) return half4(edge, edge, edge, 1);
                    if (_Debug < 2.5) return half4(nC * 0.5 + 0.5, 1);
                    if (_Debug < 3.5) return half4(frac(dC.xxx * 0.5), 1);
                    return flatAll ? half4(1, 0.5, 0, 1) : half4(edge, edge, edge, 1);
                }

                half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                // 곱하기라서 선은 언제나 원래 색보다 어둡습니다.
                col.rgb *= lerp(1.0, _Darkness, edge);
                return col;
            }
            ENDHLSL
        }
    }
    Fallback Off
}