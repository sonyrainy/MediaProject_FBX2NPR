#ifndef FBX2NPR_FACE_SDF_INCLUDED
#define FBX2NPR_FACE_SDF_INCLUDED

// 얼굴 SDF 맵을 읽어 명암을 정합니다. 다른 셰이더에 옮겨 넣을 때 이 파일만 보면 됩니다.
//
// 맵의 값은 빛의 좌우 각도입니다 (0 = 정면, 1 = 뒤). 빛이 그 값까지 돌아가는 동안 그 칸은 밝습니다.
//   R, G: 큰 명암선. R 은 빛이 얼굴 오른쪽(+right)에서 올 때, G 는 왼쪽에서 올 때.
//   B, A: 코 그림자. 채널 뜻은 같고, 그 각도를 넘으면 코 그림자가 생깁니다.
// 값 0 과 1 은 "처음부터 그늘", "끝까지 밝음"을 뜻합니다. 그래서 각도는 양 끝을 조금 남기고 담습니다 (FaceSdfCombine.Margin 과 같은 값).
//
// 빛의 위아래 각도는 쓰지 않습니다. 규칙의 큰 명암선도 수평 성분만 씁니다.
TEXTURE2D(_FaceSDF);
SAMPLER(sampler_FaceSDF);

#define FACE_SDF_MARGIN (2.0 / 255.0)

// uv 는 메시 UV0, positionWS 는 월드 위치(밝은 코 초승달용), lhN 은 빛의 수평 방향(정규화), up 은 얼굴 위 방향.
FaceShade EvalFaceSdf(float2 uv, float3 positionWS, float3 lhN, float3 up)
{
    float3 fwd   = normalize(_FaceForward.xyz);
    float3 right = normalize(cross(up, fwd));
    float  side  = dot(lhN, right);
    float  ang   = atan2(abs(side), dot(lhN, fwd)) / PI;
    ang = FACE_SDF_MARGIN + (1.0 - 2.0 * FACE_SDF_MARGIN) * ang;

    float4 t = SAMPLE_TEXTURE2D(_FaceSDF, sampler_FaceSDF, (uv - _FaceSdfRect.xy) / _FaceSdfRect.zw);
    float  tMain = (side >= 0.0) ? t.r : t.g;
    float  tNose = (side >= 0.0) ? t.b : t.a;

    // 경계 폭: 규칙의 명암선 폭(_CurveSoft, 수평 ndotl 단위 = 라디안)을 각도로 옮긴 값과 화면 1~2 픽셀 중 큰 쪽
    float dMain = tMain - ang, dNose = tNose - ang;
    float eMain = max(_CurveSoft / PI, fwidth(dMain) * 0.75);
    float eNose = max(1e-4, fwidth(dNose) * 0.75);

    FaceShade s;
    s.lit       = smoothstep(-eMain, eMain, dMain);
    s.noseShade = 1.0 - smoothstep(-eNose, eNose, dNose);

    // 명암선이 다가오면 코 그림자가 얇아져 사라진다 (ToonFaceRule.hlsl 의 NoseWane). 맵에는 없어서 여기서 뺀다.
    // 초승달은 안쪽 호가 움직이므로, 빛이 돌 때 늦게 생긴 칸부터 사라진다. born 은 이 칸이 생긴 빛의 fL (0~1).
    float wane = NoseWane(dot(lhN, fwd));
    if (_NoseMoonParam.w < 0.5)
    {
        float cosR = sqrt(saturate(1.0 - _NoseMoonParam.x * _NoseMoonParam.x));
        float born = (1.0 - cos(saturate((tNose - FACE_SDF_MARGIN) / (1.0 - 2.0 * FACE_SDF_MARGIN)) * PI)) / max(1.0 - cosR, 1e-3);
        float eW   = max(fwidth(born) * 0.75, 1e-3);
        s.noseShade *= smoothstep(-eW, eW, wane - born);
    }
    s.noseShade *= NoseFade(dot(lhN, fwd));
    // 밝은 코 초승달은 맵에 없고 규칙과 같은 식으로 실행 중에 계산한다 (ToonFaceRule.hlsl 의 NoseLitMoon)
    s.noseLit = NoseLitMoon(positionWS, lhN, up);
    s.noseDbg = 0.0; s.eyeDbg = 0.0; s.anchorDbg = 0.0;
    return s;
}

#endif
