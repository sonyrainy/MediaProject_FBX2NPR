#ifndef FBX2NPR_LINE_NORMAL_INCLUDED
#define FBX2NPR_LINE_NORMAL_INCLUDED

// 외곽선용 노멀 (월드). 노멀 버퍼에 쓰는 값이라 ToonBand 의 DepthNormals 패스와 외곽선 피처의 원본 노멀 패스(LineNormals.shader)가 같은 식을 씁니다.
// 쓰기 전에 재질 값 _OutlineFlatNormal, _InnerLineFade, _FaceWeightOn, _FaceUp, _FaceForward, _NoseTip, _NoseRadius, _NoseLineFade 를 선언해 두어야 합니다.

// 코 둘레에서 안쪽 선을 지울 비중 (0~1). 콧방울 옆·코끝의 둥근 굴곡은 소벨에 선이 아니라 넓고 흐린 회색으로 잡혀 얼룩처럼 보였다(2026-10-29).
// 영역은 리그가 코 그림자에 쓰는 코 영역(ToonBand 코 음영과 같은 타원)이다. 코 모양은 콧구멍(텍스처)과 코 그림자가 맡고, 입·눈은 영역 밖이라 선이 남는다.
// 노멀이 갑자기 바뀌면 그 경계가 선으로 잡히므로, 코 음영과 달리 뒤쪽 깊이 경계도 계단 대신 부드럽게 줄인다.
float NoseLineMask(float3 pw)
{
    if (_NoseLineFade <= 0.0) return 0.0;
    float3 up    = normalize(_FaceUp.xyz);
    float3 fwd   = normalize(_FaceForward.xyz);
    float3 right = normalize(cross(up, fwd));
    float3 rel   = pw - _NoseTip.xyz;
    float  ry    = dot(rel, up);
    float  nx    = dot(rel, right) / max(_NoseRadius.x, 1e-5);
    float  ny    = ry / max(ry > 0.0 ? _NoseRadius.y : _NoseRadius.z, 1e-5);
    float  front = smoothstep(-_NoseRadius.w, -0.6 * _NoseRadius.w, dot(rel, fwd));
    return (1.0 - smoothstep(0.55, 1.0, sqrt(nx * nx + ny * ny))) * front * _NoseLineFade;
}

float3 LineNormalWS(float3 positionOS, float3 srcOS, float2 faceW)
{
    float3 pw = TransformObjectToWorld(positionOS);
    if (_OutlineFlatNormal > 0.5)
    {
        // 정점마다 카메라를 향하는 벡터. 메시 안에서 매끄럽게 변하므로 소벨이 잡을 노멀 엣지가 없습니다.
        return normalize(_WorldSpaceCameraPos - pw);
    }
    float3 nWS = TransformObjectToWorldNormal(srcOS);
    float  t   = 0.0;
    if (_InnerLineFade.y > 1e-7)
    {
        float  dv = abs(TransformWorldToView(pw).z);
        // 픽셀 하나가 덮는 실제 크기 (미터). 원근: 2 d / (P11 x 화면 높이), 직교: 2 x 반높이 / 화면 높이
        float  fp = (unity_OrthoParams.w > 0.5) ? (2.0 * unity_OrthoParams.y / _ScreenParams.y)
                                                : (2.0 * dv / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y));
        t = (_InnerLineFade.y > _InnerLineFade.x) ? smoothstep(_InnerLineFade.x, _InnerLineFade.y, fp) : step(_InnerLineFade.y, fp);
        // 얼굴 재질을 받는 피부 메시만 지운다(리그가 _InnerLineFade 를 얼굴 재질 칸에만 넣으므로 옷 메시는 이 블록에 오지 않는다).
        // 얼굴(x)은 원래 거리에서, 나머지 피부(y: 목·손·발목 등)는 얼굴의 두 배 거리에서부터 지운다 — 얼굴만 지우면 게임 거리에서
        // 작게 보이는 피부가 선 색 덩어리로 남았고(Ch07 툰, 2026-10-14), 같은 거리로 지우면 클로즈업에서 턱선(얼굴과 목의 노멀 차)이 흐려졌다.
        // y 가 없는 예전 메시는 0 이라 x 만 쓴 것과 같고, 얼굴 정점(x=y=1)은 두 배 거리의 비중이 늘 더 작아 예전과 같다.
        if (_FaceWeightOn > 0.5)
        {
            float2 fn = _InnerLineFade.xy * 2.0;
            float  tn = (fn.y > fn.x) ? smoothstep(fn.x, fn.y, fp) : step(fn.y, fp);
            t = max(t * saturate(faceW.x), tn * saturate(faceW.y));
        }
    }
    // 코 둘레는 거리와 상관없이 지운다.
    t = max(t, NoseLineMask(pw));
    if (t > 0.0) nWS = normalize(lerp(nWS, normalize(_WorldSpaceCameraPos - pw), t));
    return nWS;
}

#endif
