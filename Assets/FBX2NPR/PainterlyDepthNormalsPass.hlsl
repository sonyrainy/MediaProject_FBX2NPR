// PainterlyDepthNormalsPass.hlsl — URP 17.3 LitDepthNormalsPass.hlsl 사본 + '멀어지면 외곽선용 노멀을 카메라 방향으로 눕히기' (기록 98번).
// 툰과 회화풍이 한 씬에 있으면 화면공간 외곽선 피처는 툰 값(노멀 엣지 0.8)으로 돈다. 툰 셰이더(ToonBand)는 얼굴이 화면에서 작아지면
// DepthNormals 패스에서 노멀을 카메라 방향으로 섞어 안쪽 선을 지우는데(_InnerLineFade), URP Lit 에는 그 장치가 없어 넓은 화면에서
// 회화풍 얼굴이 갈색 덩어리로 뭉쳤다(기록 97번 재검증). 같은 식을 회화풍 재질의 DepthNormals 에 넣는다.
// _InnerLineFade.y 가 0 이면(기본, 회화풍 혼자인 씬) 출력은 URP Lit 과 같다 — 섞는 비중이 정확히 0 이다.
#ifndef FBX2NPR_PAINTERLY_DEPTH_NORMALS_PASS_INCLUDED
#define FBX2NPR_PAINTERLY_DEPTH_NORMALS_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#if defined(LOD_FADE_CROSSFADE)
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
#endif

#if defined(_DETAIL_MULX2) || defined(_DETAIL_SCALED)
#define _DETAIL
#endif

#if defined(_PARALLAXMAP)
#define REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR
#endif

#if (defined(_NORMALMAP) || (defined(_PARALLAXMAP) && !defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR))) || defined(_DETAIL)
#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR
#endif

#if defined(_ALPHATEST_ON) || defined(_PARALLAXMAP) || defined(_NORMALMAP) || defined(_DETAIL)
#define REQUIRES_UV_INTERPOLATOR
#endif

// x = 지우기 시작, y = 다 지움 (픽셀 하나가 덮는 실제 크기, 미터). PainterlyRig 가 렌더러 프로퍼티 블록으로 넣는다 (외곽선이 노멀 엣지를 쓸 때만).
// 재질 프로퍼티가 아니므로 UnityPerMaterial 밖에 둔다 — 재질 상수 버퍼는 URP Lit 의 다른 패스(UsePass)와 같아야 한다.
float4 _InnerLineFade;

struct Attributes
{
    float4 positionOS   : POSITION;
    float4 tangentOS    : TANGENT;
    float2 texcoord     : TEXCOORD0;
    float3 normal       : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS  : SV_POSITION;
    #if defined(REQUIRES_UV_INTERPOLATOR)
    float2 uv          : TEXCOORD1;
    #endif
    half3 normalWS     : TEXCOORD2;

    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
    half4 tangentWS    : TEXCOORD4;    // xyz: tangent, w: sign
    #endif

    half3 viewDirWS    : TEXCOORD5;

    float4 fadeDirT    : TEXCOORD6;    // xyz: 정점에서 카메라로 (정규화 전), w: 카메라 방향으로 섞는 비중 0~1

    #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
    half3 viewDirTS    : TEXCOORD8;
    #endif

    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};


Varyings DepthNormalsVertex(Attributes input)
{
    Varyings output = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    #if defined(REQUIRES_UV_INTERPOLATOR)
        output.uv = TRANSFORM_TEX(input.texcoord, _BaseMap);
    #endif
    output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normal, input.tangentOS);

    output.normalWS = half3(normalInput.normalWS);
    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR) || defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
        float sign = input.tangentOS.w * float(GetOddNegativeScale());
        half4 tangentWS = half4(normalInput.tangentWS.xyz, sign);
    #endif

    #if defined(REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR)
        output.tangentWS = tangentWS;
    #endif

    #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
        half3 viewDirWS = GetWorldSpaceNormalizeViewDir(vertexInput.positionWS);
        half3 viewDirTS = GetViewDirectionTangentSpace(tangentWS, output.normalWS, viewDirWS);
        output.viewDirTS = viewDirTS;
    #endif

    // 툰 셰이더(ToonBand DepthNormals)와 같은 식. 픽셀 하나가 덮는 실제 크기 — 원근: 2 d / (P11 x 화면 높이), 직교: 2 x 반높이 / 화면 높이
    output.fadeDirT = float4(0.0, 0.0, 1.0, 0.0);
    if (_InnerLineFade.y > 1e-7)
    {
        float3 pw = vertexInput.positionWS;
        float  dv = abs(vertexInput.positionVS.z);
        float  fp = (unity_OrthoParams.w > 0.5) ? (2.0 * unity_OrthoParams.y / _ScreenParams.y)
                                                : (2.0 * dv / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y));
        float  t  = (_InnerLineFade.y > _InnerLineFade.x) ? smoothstep(_InnerLineFade.x, _InnerLineFade.y, fp) : step(_InnerLineFade.y, fp);
        output.fadeDirT = float4(_WorldSpaceCameraPos - pw, t);
    }

    return output;
}

// 비중이 0 이면 노멀을 그대로 돌려준다 (URP Lit 과 같은 출력)
float3 FBX2NPRFadeNormal(float3 n, float4 fadeDirT)
{
    if (fadeDirT.w > 0.0) n = lerp(normalize(n), normalize(fadeDirT.xyz), saturate(fadeDirT.w));
    return n;
}

void DepthNormalsFragment(
    Varyings input
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
)
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    #if defined(_ALPHATEST_ON)
        Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
    #endif

    #if defined(LOD_FADE_CROSSFADE)
        LODFadeCrossFade(input.positionCS);
    #endif

    #if defined(_GBUFFER_NORMALS_OCT)
        float3 normalWS = normalize(FBX2NPRFadeNormal(input.normalWS, input.fadeDirT));
        float2 octNormalWS = PackNormalOctQuadEncode(normalWS);           // values between [-1, +1], must use fp32 on some platforms
        float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);   // values between [ 0,  1]
        half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);      // values between [ 0,  1]
        outNormalWS = half4(packedNormalWS, 0.0);
    #else
        #if defined(_PARALLAXMAP)
            #if defined(REQUIRES_TANGENT_SPACE_VIEW_DIR_INTERPOLATOR)
                half3 viewDirTS = input.viewDirTS;
            #else
                half3 viewDirTS = GetViewDirectionTangentSpace(input.tangentWS, input.normalWS, input.viewDirWS);
            #endif
            ApplyPerPixelDisplacement(viewDirTS, input.uv);
        #endif

        #if defined(_NORMALMAP) || defined(_DETAIL)
            float sgn = input.tangentWS.w;      // should be either +1 or -1
            float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
            float3 normalTS = SampleNormal(input.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap), _BumpScale);

            #if defined(_DETAIL)
                half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, input.uv).a;
                float2 detailUv = input.uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
                normalTS = ApplyDetailNormal(detailUv, normalTS, detailMask);
            #endif

            float3 normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
        #else
            float3 normalWS = input.normalWS;
        #endif

        normalWS = FBX2NPRFadeNormal(normalWS, input.fadeDirT);
        outNormalWS = half4(NormalizeNormalPerPixel(normalWS), 0.0);
    #endif

    #ifdef _WRITE_RENDERING_LAYERS
        outRenderingLayers = EncodeMeshRenderingLayer();
    #endif
}

#endif
