#ifndef FBX2NPR_TOON_FACE_RULE_INCLUDED
#define FBX2NPR_TOON_FACE_RULE_INCLUDED

// 얼굴 명암 규칙. ToonBand 의 화면 패스와, 규칙을 얼굴 UV 에 굽는 FaceMaskBake 가 같은 식을 씁니다.
// 쓰기 전에 ToonBandInput.hlsl 을 넣어 재질 값을 선언해 두어야 합니다.

// 얼굴 한 점의 명암.
struct FaceShade
{
    float lit;         // 큰 명암선: 1 밝음, 0 그늘
    float noseShade;   // 코 그림자가 덮는 정도. 세기(_NoseShade)는 부르는 쪽이 곱한다
    float noseDbg;     // 영역 표시 (_NoseDebug)
    float eyeDbg;
    float anchorDbg;
};

// 수평 ndotl: 위 방향 성분을 빼고 방향만 비교한다.
// 수평 성분이 짧은 노멀(턱 밑·정수리)은 정규화가 불안정하므로 0 쪽으로 줄인다 (그 부위는 그림자 쪽으로 간다).
// 사각형까지의 부호 있는 거리 (안쪽이 음수). 변까지 거리의 최소값 + 교차 횟수로 안팎을 가린다 (Inigo Quilez 의 sdPolygon).
// 물방울 거리 (안쪽이 음수): 반지름 ra 인 원(pa)과 rb 인 원(pb)을 접선으로 이은 모양 (Inigo Quilez 의 sdUnevenCapsule).
// rb 를 작게 두면 pb 쪽이 뾰족한 물방울이 된다.
float SdDrop(float2 p, float2 pa, float2 pb, float ra, float rb)
{
    p -= pa; pb -= pa;
    float h = max(dot(pb, pb), 1e-12);
    float2 q = float2(dot(p, float2(pb.y, -pb.x)), dot(p, pb)) / h;
    q.x = abs(q.x);
    float b = ra - rb;
    float2 c = float2(sqrt(max(h - b * b, 1e-12)), b);
    float k = c.x * q.y - c.y * q.x;
    float m = dot(c, q), n = dot(q, q);
    if (k < 0.0) return sqrt(h * n) - ra;
    if (k > c.x) return sqrt(h * (n + 1.0 - 2.0 * q.y)) - rb;
    return m - ra;
}

float SdQuad(float2 p, float2 v0, float2 v1, float2 v2, float2 v3)
{
    float2 v[4] = { v0, v1, v2, v3 };
    float d = dot(p - v[0], p - v[0]);
    float s = 1.0;
    [unroll] for (int k = 0; k < 4; k++)
    {
        float2 vi = v[k], vj = v[(k + 3) % 4];
        float2 e = vj - vi, w = p - vi;
        float2 b = w - e * saturate(dot(w, e) / max(dot(e, e), 1e-12));
        d = min(d, dot(b, b));
        bool c0 = p.y >= vi.y, c1 = p.y < vj.y, c2 = e.x * w.y > e.y * w.x;
        if ((c0 && c1 && c2) || (!c0 && !c1 && !c2)) s = -s;
    }
    return s * sqrt(d);
}

float HorizNdotL(float3 nrm, float3 lhN, float3 up)
{
    float3 h = nrm - up * dot(nrm, up);
    return dot(h, lhN) / max(length(h), 0.35);
}

// 눈 타원 마스크 (얼굴 좌우·위 평면에서 잰 타원, 눈보다 한참 뒤쪽은 제외). 가장자리는 부드럽게 줄어든다.
// 빛 쪽으로는 타원을 끝까지 늘인다. 타원 그대로 쓰면 그림자 안에 눈만 밝은 섬이 생겼다.
// 늘이면 밝은 쪽과 눈 사이가 이어져, 명암선이 한 줄로 눈 바깥을 돌아 나간다 (레퍼런스의 모양).
float EyeMask(float3 p, float3 c, float3 right, float3 up, float3 fwd, float lightSide)
{
    float3 rel = p - c;
    float rx = dot(rel, right);
    float ex = (rx * lightSide > 0.0) ? 0.0 : rx / max(_EyeRadius.x, 1e-5);
    float ey = dot(rel, up) / max(_EyeRadius.y, 1e-5);
    float front = step(-_EyeRadius.z, dot(rel, fwd));
    // 안쪽 0.25 까지는 꽉 차고 바깥으로 천천히 준다. 0.45 에서 시작하면 효과가 가장자리에서 한꺼번에 빠져
    // 튀어나온 부분의 위아래가 수평으로 꺾였다(Ch07). 천천히 빠지면 선이 곡선으로 돌아 나온다.
    return (1.0 - smoothstep(0.25, 1.0, sqrt(ex * ex + ey * ey))) * front;
}

// 명암선이 얼굴 중앙에서 얼마나 떨어져 있나. 기준 빛(수평 35°, FBX2NPRConverter.DefaultLight)과 그보다 정면인 빛에서 1, 선이 중앙에 오면 0.
// c 는 빛의 정면 성분. 얼굴 중앙의 수평 ndotl 이 곧 c 라서, c 가 _CurveStop 까지 내려온 각도에서 선이 중앙을 지난다.
#define FACE_REF_COS 0.81915
float LineFromCenter(float c)
{
    return saturate((c - _CurveStop) / max(FACE_REF_COS - _CurveStop, 1e-4));
}

#define CHIN_MIN_WIDTH 0.12    // 턱 끝에서 남는 폭 비율. 0 이면 모든 선이 턱 끝 한 점에 모이고, 이 값만큼 선이 그림자 쪽에 남는다

// 턱 쪽 얼굴 폭 비율: 휘기 시작하는 높이에서 1, 턱 끝에서 CHIN_MIN_WIDTH.
// 타원 꼴이라 위에서는 곡선이 부드럽게 시작하고, 턱 끝에서는 턱선을 따라 눕는다. _ChinBend 0.35 면 코끝 높이에서 시작한다.
float ChinNarrow(float3 positionWS, float3 up)
{
    float span = max(_ChinScale * _ChinBend / 0.35, 1e-4);
    float t    = saturate(1.0 - (dot(positionWS - _NoseTip.xyz, up) + _ChinScale) / span);
    return pow(lerp(1.0, max(sqrt(saturate(1.0 - t * t)), CHIN_MIN_WIDTH), saturate(_CurveTilt)), max(_CurveTilt, 1.0));
}

// 턱으로 갈수록 좁아지는 얼굴에서 잰 수평 ndotl. narrow 가 1 이면 HorizNdotL 과 같다.
// 지구본의 경선이 극점으로 모이듯, 명암선이 어느 빛에서든 턱선을 따라 턱 끝으로 저절로 모인다.
// 임계값을 턱 끝에서 끌어올리는 방식은 선이 마지막에 억지로 꺾이거나, 턱 끝을 넘거나, 턱선에 부딪혀 끊겼다.
float FaceLineNdotL(float3 n, float narrow, float3 lhN, float3 up)
{
    float3 fwd   = normalize(_FaceForward.xyz);
    float3 right = normalize(cross(up, fwd));
    float3 h     = n - up * dot(n, up);
    float  hl    = length(h);
    float2 hv    = float2(dot(h, right) / narrow, dot(h, fwd));
    float2 lv    = float2(dot(lhN, right), dot(lhN, fwd));
    return dot(hv, lv) / max(length(hv), 1e-4) * (hl / max(hl, 0.35));
}

#define NOSE_WANE_FADE 4.0     // 남는 비율이 1/이 값 아래로 내려가면 농도도 함께 빠진다: 얇은 호가 실선처럼 남지 않는다
#define NOSE_RISE_FADE 2.0     // 빛이 정면에서 옆으로 갈 때 fL 이 1/이 값이 될 때까지 농도가 차오른다
#define NOSE_PULL_REACH 1.0    // 명암선이 바깥 호에서 이만큼(코 도형 폭의 배수) 가까워지면 코 쪽으로 휘기 시작한다
#define NOSE_PULL 0.7          // 명암선이 코 쪽으로 휘는 양의 상한 (코 도형 세로 반높이의 배수)
#define NOSE_PULL_UP 2.5       // 휘는 구간의 세로 폭 (코 도형 세로 반높이의 배수): 콧대 쪽
#define NOSE_PULL_DOWN 1.5     // 코 밑 쪽

// 코 높이에서 명암선의 자리: 얼굴 중앙에서 그림자 쪽으로 몇 미터인가. 코 옆 표면을 방사형 중심에서 잰 반지름(_NoseMoonExtra.y)의 구로 본다.
float LineAtNose(float c)
{
    return _NoseMoonExtra.y * (sqrt(saturate(1.0 - _CurveStop * _CurveStop)) * c - _CurveStop * sqrt(saturate(1.0 - c * c)));
}

// 명암선에서 코 도형 바깥 호까지 (m). 0 이면 닿았다.
float LineToNose(float c)
{
    return LineAtNose(c) - _NoseMoonShape.x - _NoseMoonShape.z;
}

// 명암선이 코 도형에 얼마나 다가왔나. 0 멀다, 1 바깥 호에 닿았다. 기준 빛에서는 어떤 얼굴이든 0 이다.
float NoseLineNear(float c)
{
    float size = max(_NoseMoonShape.z, 1e-5);
    return (1.0 - smoothstep(0.0, size * NOSE_PULL_REACH, LineToNose(c))) * saturate((1.0 - LineFromCenter(c)) * 2.0);
}

// 코 그림자가 남는 비율. 기준 빛과 그보다 정면인 빛에서 1, 명암선이 바깥 호에 닿을 때 0.
// 둘을 겹치면 닿는 자리가 모서리가 됐다. 그래서 코 그림자는 달이 이지러지듯 얇아져 선이 닿을 때 사라진다.
// 선이 가까이 온 뒤에야 얇아지면 빛이 6~15° 도는 사이에 갑자기 없어져서, 기준 빛부터 선이 닿을 때까지 전 구간에 나눈다.
// 기준 빛에서 이미 선이 닿아 있는 얼굴은 나눌 구간이 없으니 그대로 둔다.
// 구울 때(FACE_MASK_BAKE)는 1 이다: 맵에는 빛마다 한 번만 뒤집히는 값만 담을 수 있어서, 사라지는 쪽은 런타임에 같은 식으로 뺀다 (FaceSDF.hlsl).
float NoseWane(float c)
{
#ifdef FACE_MASK_BAKE
    return 1.0;
#else
    float gapRef = LineToNose(FACE_REF_COS);
    return (gapRef > 1e-4) ? saturate(LineToNose(c) / gapRef) : 1.0;
#endif
}

// 코 그림자 농도. 막 생기거나 사라지는 아주 얇은 호는 실선처럼 보여서 농도도 함께 뺀다. 기준 빛에서는 1. 구울 때는 칸마다 켜고 끈 값만 담으므로 1.
float NoseFade(float c)
{
#ifdef FACE_MASK_BAKE
    return 1.0;
#else
    float cosR = sqrt(saturate(1.0 - _NoseMoonParam.x * _NoseMoonParam.x));
    return saturate(NoseWane(c) * NOSE_WANE_FADE) * saturate((1.0 - c) / max(1.0 - cosR, 1e-3) * NOSE_RISE_FADE);
#endif
}

// 명암선을 코 쪽으로 살짝 휘게 하는 양 (수평 ndotl 단위). 코 그림자가 사라지는 동안 선이 그 자리를 조금 대신한다.
// 선이 코 도형까지 끌려가게 하면 그림자가 S 자로 크게 휘고 코까지 팔처럼 뻗었다. 그래서 양을 코 크기로 묶는다.
float NoseLinePull(float3 positionWS, float c, float3 up)
{
    float bN  = max(_NoseMoonShape.w, 1e-5);
    float vN  = dot(positionWS - _NoseTip.xyz, up) - _NoseMoonShape.y;
    float sig = bN * (vN > 0.0 ? NOSE_PULL_UP : NOSE_PULL_DOWN);
    float amp = min(bN * NOSE_PULL * NoseLineNear(c), max(LineAtNose(c) - _NoseMoonShape.x, 0.0));   // 코 도형 안쪽 변은 넘지 않는다
    return amp * exp(-vN * vN / (sig * sig)) / max(_NoseMoonExtra.y, 1e-3);
}

// 얼굴 한 점의 규칙 명암. n 은 정리한 노멀, origWS 는 원본 노멀, lhN 은 빛의 수평 방향(정규화), up 은 얼굴 위 방향.
FaceShade EvalFaceRule(float3 positionWS, float3 n, float3 origWS, float3 lhN, float3 up)
{
    FaceShade s;
    s.lit = 1.0; s.noseShade = 0.0; s.noseDbg = 0.0; s.eyeDbg = 0.0; s.anchorDbg = 0.0;

    // 눈 피하기: 눈 타원 안에서는 임계값을 낮춰 밝게 둔다. 마스크가 부드럽게 줄어드니
    // 명암선이 타원 가장자리를 따라 눈 바깥으로 휘어 돌고, 눈 위아래에서 원래 선으로 이어진다.
    float stopC = _CurveStop;
    // 턱 끝 아래(목)는 계속 어둡다. 지수는 8 에서 자른다: 몸통처럼 턱보다 한참 아래 정점에서 exp 가 무한대가 되면 smoothstep 이 NaN 을 낸다.
    if (_CurveTilt > 0.0001)
    {
        float hC = dot(positionWS - _NoseTip.xyz, up) + _ChinScale;           // 턱 끝 기준 높이 (m)
        stopC += _CurveTilt * max(exp(min(-hC / max(_ChinScale * _ChinBend, 1e-4), 8.0)) - 1.0, 0.0);
    }
    if (_EyeAvoid > 0.0001 || _NoseDebug > 0.5)
    {
        float3 fwdE   = normalize(_FaceForward.xyz);
        float3 rightE = normalize(cross(up, fwdE));
        float  sideL  = sign(dot(lhN, rightE));   // 빛이 얼굴 오른쪽(+1)에서 오나 왼쪽(-1)에서 오나
        float  mE     = max(EyeMask(positionWS, _EyeA.xyz, rightE, up, fwdE, sideL), EyeMask(positionWS, _EyeB.xyz, rightE, up, fwdE, sideL));
        // 1.5 로 크게 낮추면 가장자리 조금만 걸쳐도 끝까지 밝아져 경계가 타원 모양 그대로 각이 졌다.
        // 0.9 로 두면 선이 원래 자리와 타원 사이를 매끄럽게 이어 간다.
        stopC -= mE * _EyeAvoid * 0.9;
        s.eyeDbg = mE;
    }
    // 50번 기준점 명암선: 노멀이 아니라 얼굴 위의 위치로 선을 긋는다 (위의 위치·당김·눈 피하기를 모두 대신한다).
    // 처음에는 기준점에서 잰 노멀 임계값으로 그었는데, 유니티짱은 방사형 노멀 중심이 얼굴 표면 바로 뒤에 있어
    // 노멀이 옆으로 크게 퍼지는 바람에 얼굴 그림자가 통째로 사라졌다. 위치로 그으면 모든 캐릭터에서
    // 눈꼬리·턱선 기준점을 정확히 지난다 (서브컬처 게임이 얼굴 그림자를 그림으로 지정하는 것과 같은 원리).
    float litA = -1.0;
    if (_AnchorOn > 0.5)
    {
        float3 fwdA   = normalize(_FaceForward.xyz);
        float3 rightA = normalize(cross(up, fwdA));
        float  sideA  = (dot(lhN, rightA) >= 0.0) ? -1.0 : 1.0;          // 그림자 쪽 = 빛 반대. +1 이면 +X 쪽
        float3 Cp     = (sideA > 0.0) ? _AnchorCA.xyz : _AnchorCB.xyz;   // 눈꼬리 살짝 바깥
        float3 Jp     = (sideA > 0.0) ? _AnchorJA.xyz : _AnchorJB.xyz;   // 턱 끝 조금 옆 턱선
        // 얼굴 평면 좌표: u = 그림자 쪽으로 얼마나 바깥인가, v = 높이
        float uC = dot(Cp - _RadialCenter.xyz, rightA) * sideA;
        float uJ = dot(Jp - _RadialCenter.xyz, rightA) * sideA;
        float uP = dot(positionWS - _RadialCenter.xyz, rightA) * sideA;
        float vC = dot(Cp, up), vJ = dot(Jp, up), vP = dot(positionWS, up);
        float uB;
        if (vP >= vC)
        {
            // 눈꼬리 위: 끊기지 않고 거의 세로로 위까지 이어진다 (살짝 바깥으로).
            // 한때 여기서 선을 닫았더니(그 위는 밝게) 눈꼬리 높이에서 그림자가 수평으로 뚝 잘렸다.
            uB = uC + (vP - vC) * _AnchorTopSlope;
        }
        else
        {
            // 눈꼬리 아래: 볼을 타고 내려가다 턱 가까이에서 지수 꼴로 휘어 턱선 점을 지난다.
            // 정규화해서 눈꼬리 높이에서 정확히 uC, 턱선 점 높이에서 정확히 uJ.
            float lam = max((vC - vJ) * _ChinBend, 1e-4);
            float eC  = exp(-(vC - vJ) / lam);
            float eP  = exp(min((vJ - vP) / lam, 8.0));
            uB = uC + (uJ - uC) * (eP - eC) / max(1.0 - eC, 1e-4);
        }
        // 빛을 따라 움직이는 모드: 빛이 옆으로 갈수록 그림자가 커져 선이 안쪽으로, 정면 쪽이면 바깥으로 간다.
        // 기준 빛일 때 이동량이 0 이라 선이 기준점을 지난다. 고정 모드는 늘 기준점을 지난다(좌우만 바뀜).
        if (_AnchorFix < 0.5)
        {
            float3 ld  = normalize(_AnchorLight.xyz);
            float3 ldh = ld - up * dot(ld, up);
            ldh /= max(length(ldh), 1e-4);
            uB -= (abs(dot(lhN, rightA)) - abs(dot(ldh, rightA))) * _AnchorGain;
        }
        litA = smoothstep(-_AnchorSoft, _AnchorSoft, uB - uP);   // 선보다 빛 쪽이면 밝음
        s.anchorDbg = step(min(length(positionWS - Cp), length(positionWS - Jp)), 0.003);
    }
    float vMain = FaceLineNdotL(n, ChinNarrow(positionWS, up), lhN, up);
    if (_NoseMoon > 0.5) vMain -= NoseLinePull(positionWS, dot(lhN, normalize(_FaceForward.xyz)), up);
    s.lit = (litA >= 0.0) ? litA : smoothstep(stopC - _CurveSoft, stopC + _CurveSoft, vMain);
    // 54번 코 반달 → 55번 초승달: 코가 평평한 캐릭터(유니티짱)는 원본 노멀 그림자가 큰 메시 면을 따라 꺾여 사다리꼴이 됐다.
    // 그 자리·크기를 임포트 때 재 두고(FaceLandmarks.FitNoseMoon), 얼굴 평면에 초승달을 그린다.
    // 구(球)에 옆빛이 들 때의 그림자처럼 바깥 호(윤곽)와 안쪽 호(명암 경계)가 위아래 끝에서 만난다.
    // 54번은 안쪽이 곧은 변인 반달이었는데 "너무 딱 반달이라 어색"했다.
    // 빛 반대쪽에 그리고, 빛이 반대편에서 오면 거울처럼 넘어간다. 바깥 호는 고정이고
    // 안쪽 호만 빛을 따라 움직인다: 두께 ∝ (1 - 빛의 정면 성분) — 구의 명암 경계와 같은 비율.
    // 기준 빛에서 두께 = _NoseMoonParam.z, 빛이 정면이면 사라지고 옆으로 갈수록 반달(두께 1)까지 두꺼워진다.
    if (_NoseMoon > 0.5)
    {
        float3 fwdM   = normalize(_FaceForward.xyz);
        float3 rightM = normalize(cross(up, fwdM));
        float3 relM   = positionWS - _NoseTip.xyz;
        float  sideM  = dot(lhN, rightM);                       // 빛이 +right 쪽에서 오면 양수
        float  sgnM   = (sideM >= 0.0) ? -1.0 : 1.0;            // 그림자 쪽 부호
        float  cosR   = sqrt(saturate(1.0 - _NoseMoonParam.x * _NoseMoonParam.x));   // 기준 빛의 정면 성분
        float  fL     = (1.0 - dot(lhN, fwdM)) / max(1.0 - cosR, 1e-3);   // 기준 빛에서 1, 정면 빛 0, 옆으로 갈수록 커짐
        float  aM     = max(_NoseMoonShape.z, 1e-5);             // 가로 폭 (초승달은 바깥 호의 가로 반지름)
        float  bM     = max(_NoseMoonShape.w, 1e-5);             // 세로 반높이
        float  uM     = dot(relM, rightM) * sgnM - _NoseMoonShape.x;   // 콧선 쪽 기준선에서 그림자 쪽으로 (m)
        float  vM     = dot(relM, up) - _NoseMoonShape.y;
        float  dM;
        if (_NoseMoonParam.w > 2.5)
        {
            // 61번 물방울: 사용자 레퍼런스(애니 일러스트)의 코 오른쪽 그림자. 위는 뾰족(콧대 쪽), 아래는 둥글고 통통(코끝 옆),
            // 왼쪽 변은 콧대 선에 붙는다. _NosePolyA = (아래 원 중심, 위 끝 원 중심), _NosePolyParam = (아래 반지름, 위 반지름, 콧대 선 위치).
            // 콧대 선 쪽은 두고 빛이 옆으로 갈수록 뺨 쪽으로 넓어진다(최대 1.5배).
            float2 pD = float2(dot(relM, rightM) * sgnM, dot(relM, up));
            float  sD = clamp(fL, 1e-3, 1.5);
            pD.x = _NosePolyParam.z + (pD.x - _NosePolyParam.z) / sD;
            dM = -SdDrop(pD, _NosePolyA.xy, _NosePolyA.zw, _NosePolyParam.x, _NosePolyParam.y);
        }
        else if (_NoseMoonParam.w > 1.5)
        {
            // 57번 둥근 사다리꼴: 사용자가 결국 원래 원본 노멀 그림자(53번)의 사다리꼴 느낌이 가장 자연스럽다고 했다.
            // 그 윤곽을 사각형으로 줄이고 꼭짓점을 반지름만큼 둥글게 (꼭짓점은 CPU 에서 미리 안쪽으로 줄여 변 자리를 지킨다).
            // 바깥 끝은 두고, 빛이 옆으로 갈수록 콧선 쪽으로 넓어진다(원본 노멀 그림자가 커지는 방향, 최대 1.5배).
            float2 pQ = float2(dot(relM, rightM) * sgnM, dot(relM, up));
            float  sQ = clamp(fL, 1e-3, 1.5);
            pQ.x = _NosePolyParam.y - (_NosePolyParam.y - pQ.x) / sQ;
            dM = -(SdQuad(pQ, _NosePolyA.xy, _NosePolyA.zw, _NosePolyB.xy, _NosePolyB.zw) - _NosePolyParam.x);
        }
        else if (_NoseMoonParam.w > 0.5)
        {
            // 56번 세모: 콧선 쪽 세로 변 + 코끝 높이의 밑변 + 빗변, 꼭짓점은 위(콧대 쪽). 애니메이션 코 옆 그림자의 흔한 모양.
            // 밑변 폭만 빛을 따라 변한다(초승달 두께와 같은 비율, 최대 1.5배).
            float wT   = aM * min(fL, 1.5);
            float dHyp = (wT * (bM - vM) - 2.0 * bM * uM) / sqrt(wT * wT + 4.0 * bM * bM);   // 빗변 안쪽이 양수 (거리)
            dM = min(min(uM, vM + bM), dHyp);
        }
        else
        {
            // 기준 빛까지는 fL 에 비례하고 그 뒤로는 그대로 둔다. 계속 늘리면 약 40° 에서 두께가 꽉 차 곧은 변의 반달이 됐다.
            float thick = _NoseMoonParam.z * min(fL, 1.0) * NoseWane(dot(lhN, fwdM));
            float qv    = clamp(vM / bM, -1.0, 1.0);
            // 59번: 가장 두꺼운 곳을 sB 로 옮긴다(끝점 ±1 은 그대로). 음수면 코끝 쪽이 두껍고 콧대 쪽으로 가늘게 빠진다.
            float sB    = clamp(_NoseMoonExtra.x, -0.9, 0.9);
            float qw    = (qv - sB) / (1.0 - sB * qv);
            float prof  = sqrt(saturate(1.0 - qw * qw));          // 높이별 호의 가로 비율 (끝에서 0)
            float dOut  = aM * prof - uM;                         // 바깥 호 안쪽이 양수
            float dIn   = uM - aM * (1.0 - thick) * prof;         // 안쪽 호 바깥이 양수
            // 위아래 끝 밖은 확실히 바깥으로: 없으면 끝점을 잇는 세로선 위아래로 반투명 실선이 남을 수 있다
            dM = (thick > 1e-3) ? min(min(dOut, dIn), bM - abs(vM)) : -1.0;
        }
        float  eM     = max(fwidth(dM) * 0.75, 1e-6);              // 경계 1~2 픽셀 (53번과 같은 이유)
        float  shM    = smoothstep(-eM, eM, dM) * step(-_NoseMoonParam.y, dot(relM, fwdM)) * step(1e-3, fL) * NoseFade(dot(lhN, fwdM));
        s.noseShade = shM;
        s.noseDbg = shM;
    }
    // 코: 코끝 주변 타원 안에서만 원본 노멀로 그림자를 넣는다. 같은 임계값 방식이라 모양이 단순하다.
    else if (_NoseShade > 0.0001 || _NoseDebug > 0.5)
    {
        float3 fwd   = normalize(_FaceForward.xyz);
        float3 right = normalize(cross(up, fwd));
        float3 rel   = positionWS - _NoseTip.xyz;
        float  ry    = dot(rel, up);
        float  nx    = dot(rel, right) / max(_NoseRadius.x, 1e-5);
        float  ny    = ry / max(ry > 0.0 ? _NoseRadius.y : _NoseRadius.z, 1e-5);
        float  front = step(-_NoseRadius.w, dot(rel, fwd));
        float  mN    = (1.0 - smoothstep(0.55, 1.0, sqrt(nx * nx + ny * ny))) * front;
        float  sN    = HorizNdotL(normalize(origWS), lhN, up);
        // 영역 가장자리에서 그림자를 흐리게 하면 뿌옇게 번진다. 대신 임계값을 낮춰 그림자 자체가
        // 가늘어지다 사라지게 한다: 경계는 끝까지 선명하고 끝이 뾰족해진다 (mN=0 이면 임계값 -1.7 로 그림자 없음).
        float  stopN = _NoseStop - (1.0 - mN) * 2.0;
        // 52번: 경계 폭을 화면에서 1~2 픽셀 이상으로 둔다. 유니티짱처럼 작은 코는 한 픽셀 사이에 sN 이 크게 바뀌어
        // 고정 폭(_CurveSoft)으로는 경계가 픽셀보다 얇아져 계단이 졌다.
        float  eN    = max(_CurveSoft, fwidth(sN - stopN) * 0.75);
        float  shN   = 1.0 - smoothstep(stopN - eN, stopN + eN, sN);
        s.noseShade = shN;
        s.noseDbg = mN;
    }
    return s;
}

#endif
