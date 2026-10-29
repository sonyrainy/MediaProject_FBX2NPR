#ifndef FBX2NPR_LINE_NORMAL_INCLUDED
#define FBX2NPR_LINE_NORMAL_INCLUDED

// 외곽선용 노멀 (월드). 노멀 버퍼에 쓰는 값이라 ToonBand 의 DepthNormals 패스와 외곽선 피처의 원본 노멀 패스(LineNormals.shader)가 같은 식을 씁니다.
// 쓰기 전에 재질 값 _OutlineFlatNormal, _InnerLineFade, _FaceWeightOn 을 선언해 두어야 합니다.
float3 LineNormalWS(float3 positionOS, float3 srcOS, float2 faceW)
{
    if (_OutlineFlatNormal > 0.5)
    {
        // 정점마다 카메라를 향하는 벡터. 메시 안에서 매끄럽게 변하므로 소벨이 잡을 노멀 엣지가 없습니다.
        float3 pw = TransformObjectToWorld(positionOS);
        return normalize(_WorldSpaceCameraPos - pw);
    }
    float3 nWS = TransformObjectToWorldNormal(srcOS);
    if (_InnerLineFade.y > 1e-7)
    {
        float3 pw = TransformObjectToWorld(positionOS);
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
            t = max(t * saturate(faceW.x), tn * saturate(faceW.y));
        }
        nWS = normalize(lerp(nWS, normalize(_WorldSpaceCameraPos - pw), t));
    }
    return nWS;
}

#endif
