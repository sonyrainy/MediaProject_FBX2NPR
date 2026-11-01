#ifndef FBX2NPR_TOONBAND_INPUT_INCLUDED
#define FBX2NPR_TOONBAND_INPUT_INCLUDED

// ToonBand 의 재질 값. 네 패스(ToonBand, DepthNormals, ShadowCaster, DepthOnly)가 모두 이 파일을 넣어 같은 UnityPerMaterial 을 씁니다.
// 패스마다 쓰는 값만 따로 적으면 SRP Batcher 가 이 셰이더를 받지 않습니다
// (2026-11-01 Unity 검사: "UnityPerMaterial CBuffer inconsistent size inside a SubShader (DepthOnly)").
// CBUFFER_START 가 Core.hlsl 에서 오므로 그 다음에 넣습니다.
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
    float  _NoseLineFade;
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
    float  _OrigTangentSpace;
    float  _FaceWeightOn;
    float  _EmissionOn;
    float4 _EmissionColor;
    float  _UrpLighting;
    float  _LightMaxLimit;
    float  _ShadowReceive;
    float  _FaceShadowReceive;
    // 외곽선용 노멀(DepthNormals 패스, LineNormal.hlsl)
    float  _OutlineFlatNormal;
    float4 _InnerLineFade;
CBUFFER_END

#endif
