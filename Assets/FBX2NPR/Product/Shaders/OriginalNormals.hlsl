#ifndef FBX2NPR_ORIGINAL_NORMALS_INCLUDED
#define FBX2NPR_ORIGINAL_NORMALS_INCLUDED

// 변환 도구가 UV4 에 탄젠트 공간으로 실어 둔 원본 노멀을 물체 공간으로 되돌립니다.
// C# 의 OriginalNormals.Encode 와 같은 축(노멀에 직교화한 탄젠트, 탄젠트 w 부호의 바이탄젠트)을 씁니다.
// 스키닝이 돌린 노멀·탄젠트를 넣으면 원본 노멀도 뼈를 따라 돈 값이 나옵니다.
float3 DecodeOriginalNormal(float3 ts, float3 normalOS, float4 tangentOS)
{
    float3 n = normalize(normalOS);
    float3 t = tangentOS.xyz - n * dot(n, tangentOS.xyz);
    if (dot(t, t) < 1e-12)
    {
        float3 a = abs(n.x) < 0.9 ? float3(1, 0, 0) : float3(0, 1, 0);
        t = a - n * dot(n, a);
    }
    t = normalize(t);
    float3 b = cross(n, t) * (tangentOS.w < 0.0 ? -1.0 : 1.0);
    return normalize(ts.x * t + ts.y * b + ts.z * n);
}

#endif
