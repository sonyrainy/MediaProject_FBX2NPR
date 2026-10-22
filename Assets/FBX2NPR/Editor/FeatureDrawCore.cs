// FeatureDrawCore.cs — 이목구비를 사진에서 오려 남기지 않고 라벨 모양대로 '그린다' (2026-10-09, 기록 P 절 82~83번).
// 유니티에 의존하지 않는 순수 계산. 입력은 평탄화된 텍스처(피부가 한 색), 원본 알베도(색을 뽑는 용도), 텍셀별 얼굴 파싱 라벨.
//   눈썹  = 라벨을 다듬어 진한 단색 면
//   눈매  = 눈 라벨의 위쪽 경계를 따라 굵은 먹선 (가운데 굵고 양끝은 가늘게)
//   코    = 코 라벨은 전부 피부색으로 밀고, 콧구멍 자리(원본의 어두운 덩어리 두 개)에 작은 타원 둘
//   입술  = 윗입술·아랫입술 라벨 모양대로(두 입술 사이의 입 안까지 메워) 연한 단색으로 채우고, 두 라벨의 경계를 따라 가운데 입선만 가늘게 (양 끝은 가늘고 입꼬리 전에 끝난다)
//           — 처음엔 라벨 상자에 큐피드 활 템플릿을 맞췄는데 이 얼굴의 입 모양과 안 맞아 윗입술 가운데가 갈라져 보였다 (기록 95번)
// 길이는 전부 기준 길이 hwTex(눈 사이 거리의 20%, 텍셀) 대비 비율이라 텍스처 크기·캐릭터 크기에 따라 같이 커진다.
using System;
using System.Collections.Generic;

namespace FBX2NPR.EditorTools
{
    public static class FeatureDrawCore
    {
        public class Opt
        {
            public float eraseFrac = 0.4f;        // 눈썹·입술 둘레를 피부색으로 미는 반경 (×hwTex)
            public float eyeEraseFrac = 1.5f;     // 눈 둘레(눈두덩·눈 밑 그늘)를 미는 반경 (×hwTex)
            public float smoothFrac = 0.2f;       // 라벨 마스크 다듬기(닫기) 반경 (×hwTex)
            public float lashFrac = 0.30f;        // 먹선(위 아이라인) 최대 두께 (×hwTex)
            public float lashTaper = 0.30f;       // 눈 안쪽 모서리의 먹선 두께 = 최대 × 이 값
            public float lashInnerRamp = 0.40f;   // 안쪽 모서리에서 최대 두께가 될 때까지의 거리 (눈 폭 대비)
            public float lashTraceCut = 0.55f;    // 눈 구멍 찾기: 원본 밝기 < 평탄색 밝기 × 이 값 이고 눈 라벨과 이어진 영역
            public float eyeHoleReach = 2.2f;     // 눈 구멍(날개 포함)을 찾는 범위 = 눈 라벨에서 이 거리 안 (×hwTex)
            public float eyeHoleDark = 0.85f;     // 눈 구멍 색 = 원본 구멍 중앙색 × 이 값
            public float maskSmoothFrac = 0.15f;  // 눈썹·입술 라벨의 계단 가장자리를 둥글리는 반경 (×hwTex). 라벨은 1024 해상도라 2048 텍스처에서 2텍셀 계단이 난다
            public float browMaxY = 60f;          // 눈썹 색의 밝기 상한 (sRGB Y). 원본 눈썹 중앙색이 이보다 밝으면 여기까지 어둡게
            public float nostrilRx = 0.10f, nostrilRy = 0.065f;    // 콧구멍 타원 반지름 (×코 라벨 폭)
            public float nostrilDark = 0.45f;     // 콧구멍 색 = 평탄색 × 이 값
            public float nostrilCut = 0.55f;      // 콧구멍 자리 찾기: 원본 밝기 < 평탄색 밝기 × 이 값
            public float lipLighten = 0.50f;      // 입술색 = 원본 입술 중앙색을 평탄색 쪽으로 이만큼 (0 원본, 1 피부색)
            public float lipCloseFrac = 0.2f;     // 입술 라벨의 작은 틈을 잇는 닫기 반경 (×hwTex). 두 입술 사이의 입 안(라벨 없음)은 열마다 위 끝~아래 끝을 채워 메운다
            public float lipGrowFrac = 0f;        // 라벨의 아래 끝 밑으로만, 원본 색이 입술색에 가까운 텍셀을 이어 붙이는 최대 거리 (×hwTex). 0 = 끔. Ch07 은 문턱을 넘는 열이 없어 효과가 없었다
            public float lipGrowMinFrac = 0.25f;  // 아래 끝 보완은 이 깊이(×hwTex)를 넘게 이어지는 열에서만 — 온전한 쪽의 가장자리 번짐(2~5텍셀)은 건드리지 않고 잘린 곳(25텍셀)만 채운다
            public float lipMirrorFrac = 0.6f;    // 좌우 대칭으로 반대쪽을 비춰 라벨의 잘린 곳을 채우는 최대 거리 (×hwTex). 0 = 끔
            public bool lipSymmetric = false;     // 최종 채움을 얼굴 축(코 라벨 중심)에 대해 비춰 합친다. Ch07 에서는 보탠 것이 0 이라 껐다
            public bool lipMirrorAxisNose = false; // 거울 보완의 축: false = 입술 상자 가운데(사용자가 기준으로 삼은 결과), true = 코 라벨 중심 (왼쪽 윤곽까지 몇 텍셀 바꿔 되돌렸다)
            public bool lipJoinUnobserved = false; // 입술 라벨에 붙은 '관측 안 됨' 텍셀을 입술로 합치기. 입꼬리 점의 원인이 아니었고(외곽선 피처의 선이었다) 모양을 조금 바꿔 껐다
            public float lipBottomExtend = 0f;    // 모델별 손보정: 아랫입술 아래 끝을 한쪽에서만 더 내린다 (×hwTex). + 는 텍스처 x 가 큰 쪽, − 는 작은 쪽. UV 가 좌우 비대칭이라 렌더에서 한쪽 입꼬리 아래가 비는 모델용 (Ch07 0.45)
            public float lipBottomExtendFrom = 0.75f; // 손보정을 넣기 시작하는 위치 (상자 가운데 0 ~ 입꼬리 1). 입꼬리 가까이에서만 — 가운데까지 넣으면 턱 쪽으로 흘러내린다
            public float lipTipExtend = 0f;       // 모델별 손보정: 한쪽 입꼬리 끝만 바깥으로 더 늘인다 (×hwTex, + 는 텍스처 x 큰 쪽). 맨 마지막에 끝 열만 덧붙여 나머지는 그대로 (Ch07 +0.2)
            public float lipBottomExtendPeak = 0.92f; // 가장 깊게 내리는 위치 (입꼬리 바로 앞)
            public float lipTipFrac = 0f;         // 입꼬리 양 끝을 뾰족하게 더 늘이는 길이 (×hwTex). 0 = 끔 (대칭 합치기로 충분해 껐다)
            public float gapFrac = 0.06f;         // 입선 두께 (×hwTex, 가운데). 0.10 은 두 입술을 가르는 띠처럼 보였다
            public float gapTaper = 0.6f;         // 입선 양 끝 두께 = 가운데 × (1 - 이 값)
            public float gapTrim = 0.10f;         // 입선을 입술 폭의 양 끝에서 이만큼(폭 대비) 안쪽에서 끝낸다 — 입꼬리에 갈고리가 안 생기게
            public float gapFade = 0.15f;         // 입선 양 끝 이 비율 구간은 입술색과 반반 섞은 옅은 색으로 — 끝이 점처럼 보이지 않게
            public float gapSmoothFrac = 0.6f;    // 입선 경로의 이동 평균 반경 (×hwTex). 라벨 계단과 틈의 들쭉날쭉함을 편다
            public float gapDark = 0.40f;         // 입선 색 = 입술색 × 이 값
            public bool drawBrows = true, drawLash = true, drawNostrils = true, drawLips = true;
        }

        public class Result
        {
            public int browPx, lashPx, nostrilN, lipPx, erasedPx, lashTraced;   // lashTraced: 눈 구멍을 잡아 먹선을 그은 눈 수
            public bool upIsPlusY;
            public bool[] painted;   // 이 단계가 칠한 텍셀 (눈썹·눈 구멍·먹선·콧구멍·입술·입선). 뒤의 몸 평탄화가 다시 칠하지 않게 '끝난 곳'으로 표시하는 데 쓴다 — 다문 입의 틈은 라벨이 없어 표시가 안 돼 피부색 톱니가 남았다(기록 95번)
            public bool[] lipFill, lipLowerFill, lipUpperFill;   // 입술 채움 전체 / 입선 아래(아랫입술) / 입선 위(윗입술). 렌더 공간 대칭 보완(FBX2NPRLipSymmetry)이 쓴다
            public byte[] lipColor;                              // 입술 채움 색
            public List<string> log = new List<string>();
        }

        // 라벨 번호 (FaceParser.Names 순서)
        const byte LBrow = 2, RBrow = 3, LEye = 4, REye = 5, Nose = 10, Mouth = 11, ULip = 12, LLip = 13;

        /// <param name="rgba">평탄화된 텍스처 (제자리에서 고친다)</param>
        /// <param name="orig">원본 알베도 (같은 크기)</param>
        /// <param name="label">텍셀별 라벨 0~18, 255 = 없음</param>
        /// <param name="allowed">그려도 되는 텍셀 (얼굴 피부 메시가 덮고 라벨 경로가 맡은 곳)</param>
        public static Result Run(byte[] rgba, byte[] orig, int W, int H, byte[] label, bool[] allowed, byte fr, byte fg, byte fb, float hwTex, Opt o)
        {
            var res = new Result(); int N = W * H; res.painted = new bool[N];
            Action<bool[]> markP = m => { for (int i = 0; i < N; i++) if (m[i]) res.painted[i] = true; };
            Func<float, int> S = f => Math.Max(1, (int)Math.Round(f * hwTex));
            int rS = S(o.smoothFrac), rE = S(o.eraseFrac), rEye = S(o.eyeEraseFrac);

            // ---- 마스크 (다듬고, 큰 성분만, 계단 가장자리는 둥글게)
            int rSm = S(o.maskSmoothFrac);
            var brow = Smooth(Keep(Open(Close(Mask(label, allowed, N, LBrow, RBrow), W, H, rS), W, H, Math.Max(1, rS / 2)), W, H, S(0.5f) * S(0.5f), 2), W, H, rSm);
            var eye = Smooth(Keep(Close(Mask(label, allowed, N, LEye, REye), W, H, rS), W, H, S(0.5f) * S(0.5f), 2), W, H, Math.Max(1, rSm / 2));
            var lipLab = Smooth(Keep(Close(Mask(label, allowed, N, ULip, LLip, Mouth), W, H, rS), W, H, S(0.7f) * S(0.7f), 1), W, H, rSm);
            var nose = Close(Mask(label, allowed, N, Nose), W, H, rS);
            var hairLab = Mask(label, allowed, N, 17);
            int cb = Count(brow), ce = Count(eye), cl = Count(lipLab), cn = Count(nose);
            if (cb + ce + cl + cn == 0) { res.log.Add("그릴 라벨 없음"); return res; }

            // 얼굴의 '위'가 +y 인가 -y 인가: 눈썹·눈이 입술보다 위에 있다
            double yBrow = CentroidY(brow, W, H), yEye = CentroidY(eye, W, H), yLip = CentroidY(lipLab, W, H), yNose = CentroidY(nose, W, H);
            double top = !double.IsNaN(yBrow) ? yBrow : (!double.IsNaN(yEye) ? yEye : yNose), bottom = !double.IsNaN(yLip) ? yLip : yNose;
            res.upIsPlusY = !(double.IsNaN(top) || double.IsNaN(bottom)) ? top > bottom : true;
            int up = res.upIsPlusY ? 1 : -1;

            // ---- 1) 사진 조각 지우기: 눈썹·입술 둘레, 코 전체, 눈 둘레(눈 구멍은 그대로)
            var erase = Or(Or(Dilate(brow, W, H, rE), Dilate(lipLab, W, H, rE)), Or(nose, AndNot(Dilate(eye, W, H, rEye), eye)));
            for (int i = 0; i < N; i++) if (erase[i] && allowed[i]) { rgba[i * 4] = fr; rgba[i * 4 + 1] = fg; rgba[i * 4 + 2] = fb; res.erasedPx++; }

            // ---- 2) 콧구멍: 코 라벨 안에서 원본의 어두운 덩어리 둘 → 작은 타원
            if (o.drawNostrils && cn > 0)
            {
                float yFill = 0.2126f * fr + 0.7152f * fg + 0.0722f * fb;
                var dark = new bool[N];
                for (int i = 0; i < N; i++) if (nose[i]) { float y = 0.2126f * orig[i * 4] + 0.7152f * orig[i * 4 + 1] + 0.0722f * orig[i * 4 + 2]; dark[i] = y < o.nostrilCut * yFill; }
                dark = Open(dark, W, H, Math.Max(1, S(0.08f)));
                var comps = Components(dark, W, H, Math.Max(8, S(0.3f) * S(0.3f)));
                comps.Sort((a, b) => b.area.CompareTo(a.area));
                int nx0, ny0, nx1, ny1; BBox(nose, W, H, out nx0, out ny0, out nx1, out ny1);
                float noseW = Math.Max(1, nx1 - nx0 + 1);
                float rx = Math.Max(1.5f, o.nostrilRx * noseW), ry = Math.Max(1f, o.nostrilRy * noseW);
                var col = new byte[] { (byte)(fr * o.nostrilDark), (byte)(fg * o.nostrilDark), (byte)(fb * o.nostrilDark) };
                int drawn = 0;
                for (int k = 0; k < comps.Count && drawn < 2; k++)
                {
                    var c = comps[k]; var m = new bool[N];
                    Ellipse(m, W, H, c.cx, c.cy, rx, ry); And(m, allowed);
                    PaintSoft(rgba, m, W, H, col[0], col[1], col[2], 1); drawn++; markP(m);
                    res.log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "콧구멍 {0}: ({1:F0},{2:F0}) 반지름 ({3:F1},{4:F1}) 원본 어두운 덩어리 {5}텍셀", drawn, c.cx, c.cy, rx, ry, c.area));
                }
                res.nostrilN = drawn;
                if (drawn < 2) res.log.Add("콧구멍 자리를 둘 다 찾지 못함 (" + drawn + "개) — 코 라벨 " + cn + "텍셀, 어두운 덩어리 " + comps.Count + "개");
            }

            // ---- 3) 눈썹: 단색 면
            if (o.drawBrows && cb > 0)
            {
                var med = Median(orig, brow, N);
                float y = 0.2126f * med[0] + 0.7152f * med[1] + 0.0722f * med[2];
                float k = y > o.browMaxY ? o.browMaxY / y : 1f;
                var col = new byte[] { (byte)(med[0] * k), (byte)(med[1] * k), (byte)(med[2] * k) };
                PaintSoft(rgba, brow, W, H, col[0], col[1], col[2], 2); res.browPx = cb; markP(brow);
                res.log.Add("눈썹: " + cb + "텍셀, 색 (" + col[0] + "," + col[1] + "," + col[2] + ") (원본 중앙색 (" + med[0] + "," + med[1] + "," + med[2] + "))");
            }

            // ---- 4) 눈매. 눈 구멍(원본에서 눈 라벨과 이어진 어두운 영역 = 눈 구멍 안쪽 + 그 둘레에 그려진 아이라인과 날개)을 잡아
            //      (a) 구멍 전체를 한 가지 어두운 색으로 — 눈알 둘레로 보이는 아래 테가 깨끗한 선이 된다,
            //      (b) 구멍의 윗변에서 안쪽으로 먹선 — 눈 안쪽 모서리는 가늘게, 바깥쪽(날개)은 굵게. 날개는 구멍 모양을 그대로 따른다.
            //      눈 라벨은 512 해상도의 얼룩이라 그 경계를 그대로 따르면 검은 덩어리가 되고(1~2차), 원본의 어두운 띠를 그대로 칠하면 눈두덩 그늘까지 덩어리가 되고(3~4차),
            //      띠의 '눈 쪽 끝'을 눈 라벨 중심 기준으로 고르면 구멍 안쪽에서 위로 칠해 네모 덩어리가 됐다(5차).
            if (o.drawLash && ce > 0)
            {
                int T = Math.Max(2, S(o.lashFrac));
                float yFillE = 0.2126f * fr + 0.7152f * fg + 0.0722f * fb;
                var zone = AndNot(Dilate(eye, W, H, S(o.eyeHoleReach)), Or(Dilate(brow, W, H, S(0.25f)), hairLab));   // 눈썹·머리카락과 이어 붙지 않게
                var darkE = new bool[N];
                for (int i = 0; i < N; i++)
                {
                    if (!allowed[i]) continue;
                    if (eye[i]) { darkE[i] = true; continue; }
                    if (!zone[i]) continue;
                    float y = 0.2126f * orig[i * 4] + 0.7152f * orig[i * 4 + 1] + 0.0722f * orig[i * 4 + 2];
                    darkE[i] = y < o.lashTraceCut * yFillE;
                }
                var eyeComps = Components(eye, W, H, 1);
                float midX = float.NaN;
                if (eyeComps.Count >= 2) { midX = 0f; foreach (var c in eyeComps) midX += c.cx; midX /= eyeComps.Count; }
                var stroke = new bool[N]; var holeAll = new bool[N];
                foreach (var c in eyeComps)
                {
                    var hole = Close(Flood(c.has, darkE, W, H), W, H, 2);   // 넓게 흐려 다듬지 않는다 — 가는 날개 끝이 지워진다
                    int hx0, hy0, hx1, hy1; BBox(hole, W, H, out hx0, out hy0, out hx1, out hy1);
                    if (hx1 < hx0) continue;
                    for (int i = 0; i < N; i++) if (hole[i]) holeAll[i] = true;
                    bool innerAtHigh = !float.IsNaN(midX) && midX > c.cx;   // 눈 안쪽 모서리 = 두 눈의 가운데 쪽
                    float span = Math.Max(1, hx1 - hx0);
                    for (int x = hx0; x <= hx1; x++)
                    {
                        int yTop = -1;
                        for (int y = hy0; y <= hy1; y++) if (hole[y * W + x]) { if (yTop < 0 || (up > 0 ? y > yTop : y < yTop)) yTop = y; }
                        if (yTop < 0) continue;
                        float t = (x - hx0) / span;
                        float u = float.IsNaN(midX) ? 1f - Math.Abs(2f * t - 1f) : (innerAtHigh ? 1f - t : t);   // 0 = 안쪽 모서리, 1 = 바깥쪽
                        float ramp = u >= o.lashInnerRamp ? 1f : u / o.lashInnerRamp; ramp = ramp * ramp * (3f - 2f * ramp);
                        int th = Math.Max(1, (int)Math.Round(T * (o.lashTaper + (1f - o.lashTaper) * ramp)));
                        for (int d = 0; d < th; d++) { int yy = yTop - up * d; if (yy < 0 || yy >= H) break; int i = yy * W + x; if (!hole[i]) break; stroke[i] = true; }
                    }
                    res.lashTraced++;
                    res.log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "눈매: 눈 구멍 {0}텍셀 (눈 라벨 {1}텍셀에서 이어짐), 폭 {2}, 먹선 최대 {3}텍셀·안쪽 모서리 {4:F0}텍셀",
                        Count(hole), c.area, hx1 - hx0 + 1, T, T * o.lashTaper));
                }
                And(holeAll, allowed); And(stroke, allowed);
                var hm = Median(orig, holeAll, N);
                var holeCol = new byte[] { (byte)(hm[0] * o.eyeHoleDark), (byte)(hm[1] * o.eyeHoleDark), (byte)(hm[2] * o.eyeHoleDark) };
                PaintSoft(rgba, holeAll, W, H, holeCol[0], holeCol[1], holeCol[2], 1);
                PaintSoft(rgba, stroke, W, H, 24, 16, 14, 1); res.lashPx = Count(stroke); markP(holeAll); markP(stroke);
                res.log.Add("눈매: 눈 구멍 색 (" + holeCol[0] + "," + holeCol[1] + "," + holeCol[2] + "), 먹선 합계 " + res.lashPx + "텍셀");
            }

            // ---- 5) 입술: 라벨 모양대로 (윗입술 ∪ 아랫입술 ∪ 그 사이의 입 안) 연한 단색 + 두 입술 라벨의 경계를 따르는 입선
            //      입 안(다문 입의 어두운 틈)은 파서 라벨이 없어(255) '그려도 되는 텍셀'에서 빠진다. 두 입술 라벨을 닫기로 잇고 구멍을 메워 그 안까지 입술색으로 덮는다.
            if (o.drawLips && cl > 0)
            {
                var uLab = Mask(label, allowed, N, ULip); var lLab = Mask(label, allowed, N, LLip);
                // 입꼬리 끝과 다문 입의 틈은 파서가 '관측 안 됨'(255)으로 남겨 라벨에서 빠진다. 입술 라벨에 붙어 이어진 255 텍셀(라벨에서 0.3×기준길이 안)은 입술로 본다 —
                // 안 그러면 입꼬리의 어두운 사진 픽셀이 채움 밖에 남아 한쪽 입꼬리만 삐져나온다 (기록 95번 추가 3).
                var lipLabel0 = (bool[])lipLab.Clone();   // 색 표본용: 라벨 텍셀만 (입 안의 어두운 텍셀이 섞이면 입술색이 어두워진다)
                if (o.lipJoinUnobserved)
                {
                    var nearU = Dilate(lipLab, W, H, Math.Max(1, S(0.3f))); var passU = new bool[N]; int joined = 0;
                    for (int i = 0; i < N; i++) passU[i] = lipLab[i] || (nearU[i] && label[i] == 255 && !brow[i] && !eye[i] && !nose[i]);
                    var grownU = Flood(lipLab, passU, W, H);
                    for (int i = 0; i < N; i++) if (grownU[i] && !lipLab[i]) { lipLab[i] = true; joined++; }
                    res.log.Add("입술: 라벨에 붙은 '관측 안 됨' 텍셀 " + joined + "개를 입술로 포함 (입꼬리 끝·다문 입의 틈)");
                }
                // 라벨이 실제 입술보다 작게 잘린 곳(512 해상도 파싱·투영 오차 — Ch07 아랫입술 오른쪽 아래가 직선으로 끊겼다)을 원본 색으로 보완:
                // 라벨 둘레 lipGrowFrac×기준길이 안에서 원본 색도가 피부 평탄색보다 입술 중앙색에 가까운 텍셀을, 라벨에서 이어진 것만 붙인다.
                // 사용자 지적(기록 95번 추가): 색으로 키우면 입술 전체가 커져 오리 부리처럼 됐다 → 기본은 끄고(lipGrowFrac 0), 잘린 쪽만 반대쪽을 비춰 채운다(아래 lipMirrorFrac).
                var med0 = Median(orig, lipLabel0, N); int grownN = 0;
                if (o.lipGrowFrac > 0f)
                {
                    float[] chL = Chroma(med0[0], med0[1], med0[2]), chS = Chroma(fr, fg, fb);
                    float half = 0.5f * (float)Math.Sqrt((chL[0] - chS[0]) * (chL[0] - chS[0]) + (chL[1] - chS[1]) * (chL[1] - chS[1]));
                    var band = AndNot(Dilate(lipLab, W, H, S(o.lipGrowFrac)), lipLab); var pass = new bool[N];
                    // 아래쪽으로만 키운다: 열마다 라벨의 아래 끝보다 더 아래('아래' = -up)인 텍셀만 후보. 위·옆으로도 키우면 입술 전체가 커져 오리 부리처럼 됐다(사용자 지적).
                    // 렌더의 UV 대응을 찍어 보니 메시의 아래쪽 입술 한쪽이 라벨 아래 끝보다 25텍셀쯤 아래의 텍셀을 쓰고 있었다 — 그 자리는 파서가 피부로 라벨한 곳이라 비어 보였다.
                    // 열마다: 라벨 아래 끝 바로 밑에서부터 원본이 입술색인 텍셀이 몇 개 이어지는지(d) 잰다. d 가 문턱(lipGrowMinFrac×기준길이)보다 작은 열은 라벨 가장자리의 번짐일 뿐이니 건드리지 않고,
                    // 문턱을 넘는 열(진짜 잘린 곳)만 d 만큼 내린다. 사용자 지적: 왼쪽은 그대로 두고 잘린 오른쪽 아래만 채울 것 — 문턱이 없으면 온전한 쪽도 몇 텍셀씩 내려가 입술이 커진다.
                    int maxD = S(o.lipGrowFrac), thr = Math.Max(2, S(o.lipGrowMinFrac));
                    var bottomY = new int[W]; var depth = new int[W]; for (int x = 0; x < W; x++) bottomY[x] = int.MinValue;
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (lipLab[y * W + x]) { int d = -up * y; if (d > bottomY[x]) bottomY[x] = d; }
                    int cols = 0;
                    for (int x = 0; x < W; x++)
                    {
                        if (bottomY[x] == int.MinValue) continue;
                        int yb = -up * bottomY[x], d = 0;   // 라벨 아래 끝의 행
                        for (int k = 1; k <= maxD; k++)
                        {
                            int y = yb - up * k; if (y < 0 || y >= H) break; int i = y * W + x;
                            if (lipLab[i] || !allowed[i] || brow[i] || eye[i] || nose[i]) break;
                            float[] c = Chroma(orig[i * 4], orig[i * 4 + 1], orig[i * 4 + 2]);
                            if (Math.Sqrt((c[0] - chL[0]) * (c[0] - chL[0]) + (c[1] - chL[1]) * (c[1] - chL[1])) >= half) break;
                            d = k;
                        }
                        depth[x] = d >= thr ? d : 0; if (depth[x] > 0) cols++;
                    }
                    // 가로로 이동 평균해 내린 곳과 안 내린 곳의 경계를 비스듬히
                    int rwG = Math.Max(1, S(0.3f)); var depthS = new float[W];
                    for (int x = 0; x < W; x++) { if (bottomY[x] == int.MinValue) continue; float s = 0; int n = 0; for (int dd = -rwG; dd <= rwG; dd++) { int xx = x + dd; if (xx < 0 || xx >= W || bottomY[xx] == int.MinValue) continue; s += depth[xx]; n++; } depthS[x] = n > 0 ? s / n : 0f; }
                    for (int x = 0; x < W; x++)
                    {
                        int g = (int)Math.Round(depthS[x]); if (g <= 0 || bottomY[x] == int.MinValue) continue;
                        int yb = -up * bottomY[x];
                        for (int k = 1; k <= g; k++) { int y = yb - up * k; if (y < 0 || y >= H) break; int i = y * W + x; if (!lipLab[i] && !brow[i] && !eye[i] && !nose[i]) { lipLab[i] = true; grownN++; } }
                    }
                    res.log.Add("입술: 아래 끝 보완 — 문턱 " + thr + "텍셀 넘는 열 " + cols + ", 보탠 텍셀 " + grownN);
                }
                // 입술은 좌우 대칭이므로, 라벨 상자의 가운데 세로축에 대해 반대쪽을 비춰 라벨에 없는 텍셀을 보탠다. 온전한 쪽은 비춰도 거의 겹치므로 바뀌지 않고, 잘린 쪽만 채워진다.
                // 라벨에서 lipMirrorFrac×기준길이 안의 텍셀만 보태 UV 가 조금 비대칭이어도 큰 덩어리가 안 생긴다.
                int mirrorN = 0;
                if (o.lipMirrorFrac > 0f)
                {
                    int bx0, by0, bx1, by1; BBox(lipLab, W, H, out bx0, out by0, out bx1, out by1);
                    if (bx1 > bx0)
                    {
                        // 대칭축: 코 라벨의 무게중심 x (얼굴 축). 입술 상자의 가운데를 쓰면 한쪽 입꼬리가 잘린 만큼 축이 치우쳐 비친 끝이 모자란다 (기록 95번 추가 3)
                        float axis = 0.5f * (bx0 + bx1); if (o.lipMirrorAxisNose && cn > 0) { double sx = 0; long nn = 0; for (int i = 0; i < N; i++) if (nose[i]) { sx += i % W; nn++; } if (nn > 0) axis = (float)(sx / nn); }
                        var near = AndNot(Dilate(lipLab, W, H, S(o.lipMirrorFrac)), lipLab); var add = new bool[N]; int cx2 = (int)Math.Round(2f * axis);
                        res.log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "입술: 대칭축 x={0:F1} (코 라벨 {1}, 상자 가운데 {2:F1})", axis, cn > 0 ? "기준" : "없음", 0.5f * (bx0 + bx1)));
                        for (int y = by0; y <= by1; y++) for (int x = bx0; x <= bx1; x++)
                        {
                            int i = y * W + x; if (!lipLab[i]) continue;
                            int xm = cx2 - x; if (xm < 0 || xm >= W) continue; int j = y * W + xm;
                            if (!lipLab[j] && near[j] && !brow[j] && !eye[j] && !nose[j]) add[j] = true;   // allowed 를 묻지 않는다: 입꼬리의 '관측 안 됨' 텍셀도 채워야 한다
                        }
                        for (int i = 0; i < N; i++) if (add[i]) { lipLab[i] = true; mirrorN++; }
                    }
                }
                // 입은 세로로 볼록한 모양이라, 열마다 라벨의 위 끝과 아래 끝 사이를 다 채우면 입 안(라벨 없음)까지 메워진다. 닫기(FillHoles)는 틈이 입꼬리로 새어 톱니가 남았다.
                var lipFull = Keep(Smooth(SpanFill(Close(lipLab, W, H, Math.Max(1, S(o.lipCloseFrac))), W, H), W, H, rSm), W, H, 1, 1);
                int lx0, ly0, lx1, ly1; BBox(lipFull, W, H, out lx0, out ly0, out lx1, out ly1);
                // 입꼬리 끝 늘이기: 렌더에서 보이는 입선은 텍스처의 선이 아니라 메시의 입 주름을 따라 화면공간 외곽선이 그리는 안쪽 선이고, 그 끝이 라벨 채움보다 바깥에 있어
                // 한쪽 입꼬리가 채움 밖에 남았다(기록 95번 추가 3). 양 끝을 lipTipFrac×기준길이만큼 뾰족하게 더 늘여 주름 끝까지 덮는다.
                // 사용자 지시(기록 95번 추가 4): "복잡하게 말고 좌우 대칭을 맞춰라" — 최종 채움을 얼굴 축에 대해 비춰 합친다. 온전한 쪽의 모양이 잘린 쪽에 그대로 채워진다.
                if (o.lipSymmetric)
                {
                    float axisF = 0.5f * (lx0 + lx1); if (cn > 0) { double sx = 0; long nn = 0; for (int i = 0; i < N; i++) if (nose[i]) { sx += i % W; nn++; } if (nn > 0) axisF = (float)(sx / nn); }
                    int cxx = (int)Math.Round(2f * axisF); var mir = new bool[N]; int added = 0;
                    for (int y = ly0; y <= ly1; y++) for (int x = lx0; x <= lx1; x++) { if (!lipFull[y * W + x]) continue; int xm = cxx - x; if (xm >= 0 && xm < W) mir[y * W + xm] = true; }
                    for (int i = 0; i < N; i++) if (mir[i] && !lipFull[i] && !brow[i] && !eye[i] && !nose[i]) { lipFull[i] = true; added++; }
                    lipFull = Keep(Smooth(lipFull, W, H, Math.Max(1, rSm / 2)), W, H, 1, 1);
                    BBox(lipFull, W, H, out lx0, out ly0, out lx1, out ly1);
                    res.log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "입술: 좌우 대칭 합치기 — 축 x={0:F1}, 보탠 텍셀 {1}", axisF, added));
                }
                // 모델별 손보정: 한쪽 아랫입술 아래 끝을 입꼬리 쪽으로 갈수록 더 내린다 (사용자가 칠한 오른쪽 아래). 상자 가운데에서 입꼬리까지 u=0~1, 0.3 부터 사인 혹으로 올라가 0.65 에서 최대, 끝에서 0.
                if (Math.Abs(o.lipBottomExtend) > 0.001f && lx1 > lx0)
                {
                    int Dmax = S(Math.Abs(o.lipBottomExtend)); float mid = 0.5f * (lx0 + lx1); int xEnd = o.lipBottomExtend > 0f ? lx1 : lx0; int extN = 0;
                    var depthE = new float[W];
                    // u0 부터 올라가 up 에서 최대, 입꼬리 끝(1)에서 최대의 1/4. 렌더의 UV 대응으로 잰 결손은 입꼬리 바로 앞(u≈0.9)에서 가장 깊었다. 0.3~1 사인 혹은 아랫입술 가운데가 턱 쪽으로 흘러내렸고, 0.7~1 혹은 작은 탭처럼 보였다.
                    float u0 = o.lipBottomExtendFrom, upk = o.lipBottomExtendPeak;
                    for (int x = lx0; x <= lx1; x++)
                    {
                        float u = (x - mid) / (xEnd - mid); if (u < u0 || u > 1f) continue;
                        float r = u <= upk ? (float)Math.Sin(0.5 * Math.PI * (u - u0) / Math.Max(1e-3f, upk - u0)) : 1f - 0.75f * (u - upk) / Math.Max(1e-3f, 1f - upk);
                        depthE[x] = Dmax * Math.Max(0f, r);
                    }
                    for (int x = lx0; x <= lx1; x++)
                    {
                        int g = (int)Math.Round(depthE[x]); if (g <= 0) continue;
                        int bot = -1; for (int y = ly0; y <= ly1; y++) if (lipFull[y * W + x]) { int d = -up * y; if (bot < 0 || d > -up * bot) bot = y; }
                        if (bot < 0) continue;
                        for (int k = 1; k <= g; k++) { int y = bot - up * k; if (y < 0 || y >= H) break; int i = y * W + x; if (!lipFull[i] && !brow[i] && !eye[i] && !nose[i]) { lipFull[i] = true; extN++; } }
                    }
                    lipFull = Keep(Smooth(lipFull, W, H, Math.Max(1, rSm / 2)), W, H, 1, 1);
                    BBox(lipFull, W, H, out lx0, out ly0, out lx1, out ly1);
                    res.log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "입술: 아래 끝 손보정 {0:+0.00;-0.00}×기준길이 (최대 {1}텍셀, 텍스처 {2}쪽) — 보탠 텍셀 {3}", o.lipBottomExtend, Dmax, o.lipBottomExtend > 0f ? "오른" : "왼", extN));
                }
                int ext = S(o.lipTipFrac);
                if (ext > 0 && lx1 - lx0 > 4 * ext)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        int dir = side == 0 ? -1 : 1, xi = side == 0 ? lx0 + ext : lx1 - ext;
                        int tTop = -1, tBot = -1; for (int y = ly0; y <= ly1; y++) if (lipFull[y * W + xi]) { if (tTop < 0) tTop = y; tBot = y; }
                        if (tTop < 0) continue;
                        float c = 0.5f * (tTop + tBot), half = 0.5f * (tBot - tTop);
                        for (int k = 1; k <= 2 * ext; k++)
                        {
                            int x = xi + dir * k; if (x < 0 || x >= W) break;
                            float f = 1f - k / (2f * ext + 1f); int h = (int)Math.Round(half * f);
                            for (int y = (int)Math.Round(c) - h; y <= (int)Math.Round(c) + h; y++) if (y >= 0 && y < H) lipFull[y * W + x] = true;
                        }
                    }
                    BBox(lipFull, W, H, out lx0, out ly0, out lx1, out ly1);
                }
                var lipCore = Erode(lipFull, W, H, rSm + 1);   // 가장자리 둥글린 테를 뺀 안쪽 — 틈 찾기용
                if (lx1 >= lx0)
                {
                    var med = Median(orig, lipLabel0, N);
                    var col = new byte[] { (byte)(med[0] + (fr - med[0]) * o.lipLighten), (byte)(med[1] + (fg - med[1]) * o.lipLighten), (byte)(med[2] + (fb - med[2]) * o.lipLighten) };
                    var gap = new byte[] { (byte)(col[0] * o.gapDark), (byte)(col[1] * o.gapDark), (byte)(col[2] * o.gapDark) };
                    // 입술 둘레의 사진 조각(라벨 밖으로 삐져나온 입술)은 피부색으로
                    var under = AndNot(Dilate(lipFull, W, H, rE), lipFull);
                    for (int i = 0; i < N; i++) if (under[i] && (allowed[i] || label[i] == 255)) { rgba[i * 4] = fr; rgba[i * 4 + 1] = fg; rgba[i * 4 + 2] = fb; res.painted[i] = true; }   // '관측 안 됨' 텍셀의 사진 조각도 민다
                    PaintSoft(rgba, lipFull, W, H, col[0], col[1], col[2], 1); res.lipPx = Count(lipFull); markP(lipFull);
                    // 입선: 열마다 (1) 입술 모양 안쪽에 라벨 없는 틈(다문 입의 어두운 안쪽, 또는 '입 안' 라벨)이 있으면 그 세로 가운데,
                    //       (2) 없으면 윗입술 라벨의 아래 끝과 아랫입술 라벨의 위 끝의 가운데 ('아래' = -up 쪽), (3) 둘 다 없으면 입술 모양의 세로 가운데. 그 밖의 열은 이웃에서 보간.
                    //       아랫입술 라벨이 틈 위쪽에도 얇게 붙어 있어 (2)만 쓰면 선이 틈 위에 그려졌다 (1차 시도).
                    int span = lx1 - lx0 + 1, trim = Math.Max(1, (int)Math.Round(span * o.gapTrim));
                    var gy = new float[W]; var has = new bool[W]; int both = 0, gapCols = 0;
                    for (int x = lx0; x <= lx1; x++)
                    {
                        int uEnd = -1, lStart = -1, fTop = -1, fBot = -1; long gSum = 0; int gN = 0;
                        for (int y = ly0; y <= ly1; y++)
                        {
                            int i = y * W + x; int down = -up * y;   // 클수록 아래
                            if (uLab[i] && (uEnd < 0 || down > -up * uEnd)) uEnd = y;
                            if (lLab[i] && (lStart < 0 || down < -up * lStart)) lStart = y;
                            if (lipFull[i]) { if (fTop < 0) fTop = y; fBot = y; }
                            if (lipCore[i] && !uLab[i] && !lLab[i]) { gSum += y; gN++; }
                        }
                        if (gN > 0) { gy[x] = gSum / (float)gN + 0.5f; has[x] = true; gapCols++; }
                        else if (uEnd >= 0 && lStart >= 0) { gy[x] = 0.5f * (uEnd + lStart) + 0.5f; has[x] = true; both++; }
                        else if (fTop >= 0) { gy[x] = 0.5f * (fTop + fBot) + 0.5f; has[x] = false; }
                    }
                    if (gapCols + both == 0) for (int x = lx0; x <= lx1; x++) has[x] = gy[x] > 0f;
                    // 빈 열은 양옆에서 보간
                    for (int x = lx0; x <= lx1; x++)
                    {
                        if (has[x]) continue;
                        int a = x - 1, b = x + 1; while (a >= lx0 && !has[a]) a--; while (b <= lx1 && !has[b]) b++;
                        if (a >= lx0 && b <= lx1) { gy[x] = gy[a] + (gy[b] - gy[a]) * (x - a) / (float)(b - a); }
                        else if (a >= lx0) gy[x] = gy[a]; else if (b <= lx1) gy[x] = gy[b]; else continue;
                        has[x] = true;
                    }
                    // 이동 평균으로 계단을 없앤다
                    int rw = Math.Max(1, S(o.gapSmoothFrac)); var sm = new float[W];
                    for (int x = lx0; x <= lx1; x++) { float s = 0; int n = 0; for (int d = -rw; d <= rw; d++) { int xx = x + d; if (xx < lx0 || xx > lx1 || !has[xx]) continue; s += gy[xx]; n++; } sm[x] = n > 0 ? s / n : gy[x]; }
                    var gapM = new bool[N]; var gapEnd = new bool[N]; int gw = S(o.gapFrac); int x0g = lx0 + trim, x1g = lx1 - trim;
                    for (int x = x0g + 1; x <= x1g; x++)
                    {
                        if (!has[x - 1] || !has[x]) continue;
                        float t = (x - x0g) / (float)Math.Max(1, x1g - x0g);                       // 0~1
                        float tap = 1f - o.gapTaper * Math.Abs(2f * t - 1f);                        // 가운데 1, 양 끝 1 - taper
                        bool end = t < o.gapFade || t > 1f - o.gapFade;                             // 양 끝은 옅게 — 끝이 점처럼 보이지 않게
                        Segment(end ? gapEnd : gapM, W, H, x - 0.5f, sm[x - 1], x + 0.5f, sm[x], Math.Max(1, (int)Math.Round(gw * tap)));
                    }
                    And(gapM, lipFull); And(gapEnd, lipFull);   // 입술 밖으로는 안 나간다 → 입꼬리 갈고리 없음
                    var gapSoft = new byte[] { (byte)((col[0] + gap[0]) / 2), (byte)((col[1] + gap[1]) / 2), (byte)((col[2] + gap[2]) / 2) };
                    PaintSoft(rgba, gapEnd, W, H, gapSoft[0], gapSoft[1], gapSoft[2], 1); markP(gapEnd);
                    PaintSoft(rgba, gapM, W, H, gap[0], gap[1], gap[2], 1); markP(gapM);
                    // 렌더 공간 대칭 보완용 마스크: 입선 경로(sm) 기준으로 아래(아랫입술)/위(윗입술). 입선이 없는 열은 가장 가까운 열의 값을 쓴다.
                    {
                        res.lipFill = (bool[])lipFull.Clone(); res.lipLowerFill = new bool[N]; res.lipUpperFill = new bool[N]; res.lipColor = new[] { col[0], col[1], col[2] };
                        var lineY = new float[W]; int lastHas = -1;
                        for (int x = lx0; x <= lx1; x++) { if (has[x]) lastHas = x; lineY[x] = lastHas >= 0 ? sm[lastHas] : float.NaN; }
                        for (int x = lx1; x >= lx0; x--) { if (!float.IsNaN(lineY[x])) continue; int b = x + 1; while (b <= lx1 && float.IsNaN(lineY[b])) b++; lineY[x] = b <= lx1 ? lineY[b] : 0.5f * (ly0 + ly1); }
                        for (int y = ly0; y <= ly1; y++) for (int x = lx0; x <= lx1; x++)
                        {
                            int i = y * W + x; if (!lipFull[i]) continue;
                            bool below = -up * (y + 0.5f) > -up * lineY[x];   // '아래' = -up 쪽
                            if (below) res.lipLowerFill[i] = true; else res.lipUpperFill[i] = true;
                        }
                    }
                    // 모델별 손보정, 맨 마지막에: 한쪽 입꼬리 끝만 바깥으로 lipTipExtend×기준길이 더 늘인다 (+ 는 텍스처 x 큰 쪽). 다른 쪽과 나머지 텍셀은 비트 단위로 그대로다 —
                    // 메시의 입 주름(화면공간 안쪽 선)이 채움보다 바깥까지 뻗어 사이에 살색이 비는 모델용(Ch07 화면 오른쪽). 끝 열의 세로 구간을 바깥으로 이어 붙이고 끝으로 갈수록 가늘게.
                    if (Math.Abs(o.lipTipExtend) > 0.001f && lx1 - lx0 > 8)
                    {
                        int E = S(Math.Abs(o.lipTipExtend)); int dir = o.lipTipExtend > 0f ? 1 : -1; int xEnd = dir > 0 ? lx1 : lx0;
                        int xs = xEnd - dir * 2; if (xs < 0 || xs >= W) xs = xEnd;   // 끝에서 2텍셀 안쪽 열의 세로 구간 (끝 열은 1~2텍셀뿐이라)
                        int tTop = -1, tBot = -1; for (int y = ly0; y <= ly1; y++) if (lipFull[y * W + xs]) { if (tTop < 0) tTop = y; tBot = y; }
                        var tipAdd = new bool[N]; int addN = 0;
                        if (tTop >= 0)
                        {
                            // 입 주름 선 쪽(아래)으로 1텍셀 내린 중심에서, 안쪽 열 두께의 60% 로 시작해 끝으로 갈수록 가늘게 — 두껍게 하면 렌더에서 혹처럼 보였다
                            float c = 0.5f * (tTop + tBot) - up * 1f, h0 = Math.Max(1f, 0.3f * (tBot - tTop));
                            for (int e = 1; e <= E; e++)
                            {
                                int x = xEnd + dir * e; if (x < 0 || x >= W) break;
                                int h = Math.Max(0, (int)Math.Round(h0 * (1f - e / (E + 1f))));
                                for (int y = (int)Math.Round(c) - h; y <= (int)Math.Round(c) + h; y++) { if (y < 0 || y >= H) continue; int i = y * W + x; if (!lipFull[i] && !brow[i] && !eye[i] && !nose[i]) { tipAdd[i] = true; addN++; } }
                            }
                        }
                        if (addN > 0) { PaintSoft(rgba, tipAdd, W, H, col[0], col[1], col[2], 1); markP(tipAdd); }
                        res.log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "입술: 입꼬리 끝 늘이기 {0:+0.00;-0.00}×기준길이 ({1}텍셀, 텍스처 {2}쪽) — 보탠 텍셀 {3}", o.lipTipExtend, E, dir > 0 ? "오른" : "왼", addN));
                    }
                    res.log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "입술: 라벨 모양 {0}x{1} ({2}텍셀, 윗입술 {3}·아랫입술 {4}, 원본 색 보완 {16}·대칭 보완 {17}), 색 ({5},{6},{7}) (원본 중앙색 ({8},{9},{10})), 입선 두께 {11}텍셀 — 틈 가운데 {12}열·라벨 경계 {13}열 / {14}열, {15}텍셀",
                        span, ly1 - ly0 + 1, res.lipPx, Count(uLab), Count(lLab), col[0], col[1], col[2], med[0], med[1], med[2], gw, gapCols, both, span, Count(gapM), grownN, mirrorN));
                }
            }
            return res;
        }

        /// <summary>밝기를 뺀 색(색도): r/(r+g+b), g/(r+g+b).</summary>
        static float[] Chroma(float r, float g, float b) { float s = Math.Max(r + g + b, 1e-3f); return new[] { r / s, g / s }; }

        /// <summary>열마다 마스크의 위 끝과 아래 끝 사이를 다 채운다 (세로로 볼록한 모양용: 입술).</summary>
        static bool[] SpanFill(bool[] m, int W, int H)
        {
            var o = new bool[m.Length];
            for (int x = 0; x < W; x++)
            {
                int top = -1, bot = -1;
                for (int y = 0; y < H; y++) if (m[y * W + x]) { if (top < 0) top = y; bot = y; }
                if (top < 0) continue;
                for (int y = top; y <= bot; y++) o[y * W + x] = true;
            }
            return o;
        }

        /// <summary>마스크의 바깥에서 닿지 않는 빈 곳(구멍)을 채운다. 다문 입의 틈처럼 라벨이 없는 안쪽을 메우는 데 쓴다.</summary>
        static bool[] FillHoles(bool[] m, int W, int H)
        {
            int N = W * H; var pass = Not(m); var seed = new bool[N];
            for (int x = 0; x < W; x++) { if (pass[x]) seed[x] = true; if (pass[(H - 1) * W + x]) seed[(H - 1) * W + x] = true; }
            for (int y = 0; y < H; y++) { if (pass[y * W]) seed[y * W] = true; if (pass[y * W + W - 1]) seed[y * W + W - 1] = true; }
            var outside = Flood(seed, pass, W, H);
            var o = new bool[N]; for (int i = 0; i < N; i++) o[i] = m[i] || !outside[i];
            return o;
        }

        // ---------- 마스크 도구 ----------
        class Comp { public int area, x0, y0, x1, y1; public float cx, cy; public bool[] has; }

        static bool[] Mask(byte[] label, bool[] allowed, int N, params byte[] ks)
        {
            var m = new bool[N];
            for (int i = 0; i < N; i++) { if (!allowed[i]) continue; byte l = label[i]; for (int k = 0; k < ks.Length; k++) if (l == ks[k]) { m[i] = true; break; } }
            return m;
        }
        static bool[] Dilate(bool[] m, int W, int H, int r)
        {
            var t = new bool[W * H]; var o = new bool[W * H];
            for (int y = 0; y < H; y++) { int row = y * W; for (int x = 0; x < W; x++) { bool a = false; for (int xx = Math.Max(0, x - r); xx <= Math.Min(W - 1, x + r); xx++) if (m[row + xx]) { a = true; break; } t[row + x] = a; } }
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) { bool a = false; for (int yy = Math.Max(0, y - r); yy <= Math.Min(H - 1, y + r); yy++) if (t[yy * W + x]) { a = true; break; } o[y * W + x] = a; }
            return o;
        }
        static bool[] Erode(bool[] m, int W, int H, int r) { var inv = Not(m); var d = Dilate(inv, W, H, r); return Not(d); }
        /// <summary>seed 에서 시작해 pass 인 텍셀로만 8방향으로 번진 영역</summary>
        static bool[] Flood(bool[] seed, bool[] pass, int W, int H)
        {
            int N = W * H; var o = new bool[N]; var q = new int[N]; int qh = 0, qt = 0;
            for (int i = 0; i < N; i++) if (seed[i] && pass[i]) { o[i] = true; q[qt++] = i; }
            while (qh < qt)
            {
                int p = q[qh++]; int x = p % W, y = p / W;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                    int k = yy * W + xx; if (o[k] || !pass[k]) continue; o[k] = true; q[qt++] = k;
                }
            }
            return o;
        }
        /// <summary>계단 가장자리를 둥글린다: 박스 흐림 두 번 뒤 0.5 문턱. 넓이는 거의 그대로, 모서리만 둥글어진다.</summary>
        static bool[] Smooth(bool[] m, int W, int H, int r)
        {
            if (r < 1) return m;
            int N = W * H; var f = new float[N]; for (int i = 0; i < N; i++) f[i] = m[i] ? 1f : 0f;
            var a = new float[N]; var t = new float[N]; Box(f, a, t, W, H, r); Box(a, f, t, W, H, r);
            var o = new bool[N]; for (int i = 0; i < N; i++) o[i] = f[i] >= 0.5f; return o;
        }
        static bool[] Close(bool[] m, int W, int H, int r) { return Erode(Dilate(m, W, H, r), W, H, r); }
        static bool[] Open(bool[] m, int W, int H, int r) { return Dilate(Erode(m, W, H, r), W, H, r); }
        static bool[] Not(bool[] a) { var o = new bool[a.Length]; for (int i = 0; i < a.Length; i++) o[i] = !a[i]; return o; }
        static bool[] Or(bool[] a, bool[] b) { var o = new bool[a.Length]; for (int i = 0; i < a.Length; i++) o[i] = a[i] || b[i]; return o; }
        static bool[] AndNot(bool[] a, bool[] b) { var o = new bool[a.Length]; for (int i = 0; i < a.Length; i++) o[i] = a[i] && !b[i]; return o; }
        static void And(bool[] a, bool[] b) { for (int i = 0; i < a.Length; i++) a[i] = a[i] && b[i]; }
        static int Count(bool[] a) { int c = 0; foreach (var v in a) if (v) c++; return c; }
        static double CentroidY(bool[] m, int W, int H) { double s = 0; long n = 0; for (int i = 0; i < m.Length; i++) if (m[i]) { s += i / W; n++; } return n > 0 ? s / n : double.NaN; }
        static void BBox(bool[] m, int W, int H, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = W; y0 = H; x1 = -1; y1 = -1;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) if (m[y * W + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        }
        static List<Comp> Components(bool[] m, int W, int H, int minArea)
        {
            int N = W * H; var lab = new int[N]; var list = new List<Comp>(); var stack = new int[N]; int id = 0;
            for (int s = 0; s < N; s++)
            {
                if (!m[s] || lab[s] != 0) continue;
                id++; int sp = 0; stack[sp++] = s; lab[s] = id; var members = new List<int>();
                while (sp > 0)
                {
                    int p = stack[--sp]; members.Add(p); int x = p % W, y = p / W;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        int q = yy * W + xx; if (m[q] && lab[q] == 0) { lab[q] = id; stack[sp++] = q; }
                    }
                }
                if (members.Count < minArea) continue;
                var c = new Comp { area = members.Count, x0 = W, y0 = H, x1 = -1, y1 = -1, has = new bool[N] };
                double sx = 0, sy = 0;
                foreach (var p in members) { int x = p % W, y = p / W; c.has[p] = true; sx += x; sy += y; if (x < c.x0) c.x0 = x; if (x > c.x1) c.x1 = x; if (y < c.y0) c.y0 = y; if (y > c.y1) c.y1 = y; }
                c.cx = (float)(sx / members.Count); c.cy = (float)(sy / members.Count);
                list.Add(c);
            }
            return list;
        }
        static bool[] Keep(bool[] m, int W, int H, int minArea, int largest)
        {
            var comps = Components(m, W, H, Math.Max(1, minArea)); comps.Sort((a, b) => b.area.CompareTo(a.area));
            var o = new bool[m.Length];
            for (int k = 0; k < comps.Count && (largest <= 0 || k < largest); k++) { var h = comps[k].has; for (int i = 0; i < o.Length; i++) if (h[i]) o[i] = true; }
            return o;
        }
        static int[] Median(byte[] px, bool[] m, int N)
        {
            var hr = new int[256]; var hg = new int[256]; var hb = new int[256]; int c = 0;
            for (int i = 0; i < N; i++) if (m[i]) { hr[px[i * 4]]++; hg[px[i * 4 + 1]]++; hb[px[i * 4 + 2]]++; c++; }
            return new[] { Pct(hr, c), Pct(hg, c), Pct(hb, c) };
        }
        static int Pct(int[] h, int c) { int acc = 0; for (int i = 0; i < 256; i++) { acc += h[i]; if (acc * 2 >= c) return i; } return 0; }

        /// <summary>마스크 가장자리를 feather 텍셀만큼 부드럽게 해서 칠한다.</summary>
        static void PaintSoft(byte[] px, bool[] m, int W, int H, byte r, byte g, byte b, int feather)
        {
            int N = W * H; var f = new float[N]; for (int i = 0; i < N; i++) f[i] = m[i] ? 1f : 0f;
            var a = new float[N]; var t = new float[N];
            for (int k = 0; k < feather; k++) { Box(f, a, t, W, H, 1); var sw = f; f = a; a = sw; }
            for (int i = 0; i < N; i++)
            {
                float v = f[i]; if (v <= 0f) continue; if (m[i]) v = 1f;
                px[i * 4] = (byte)Math.Round(px[i * 4] + (r - px[i * 4]) * v); px[i * 4 + 1] = (byte)Math.Round(px[i * 4 + 1] + (g - px[i * 4 + 1]) * v); px[i * 4 + 2] = (byte)Math.Round(px[i * 4 + 2] + (b - px[i * 4 + 2]) * v);
            }
        }
        static void Box(float[] s, float[] d, float[] t, int W, int H, int r)
        {
            double inv = 1.0 / (2 * r + 1);
            for (int y = 0; y < H; y++) { int row = y * W; double acc = 0; for (int x = 0; x <= r && x < W; x++) acc += s[row + x]; for (int x = 0; x < W; x++) { t[row + x] = (float)(acc * inv); int xo = x - r, xn = x + r + 1; if (xo >= 0) acc -= s[row + xo]; if (xn < W) acc += s[row + xn]; } }
            for (int x = 0; x < W; x++) { double acc = 0; for (int y = 0; y <= r && y < H; y++) acc += t[y * W + x]; for (int y = 0; y < H; y++) { d[y * W + x] = (float)(acc * inv); int yo = y - r, yn = y + r + 1; if (yo >= 0) acc -= t[yo * W + x]; if (yn < H) acc += t[yn * W + x]; } }
        }

        // ---------- 도형 ----------
        static void Ellipse(bool[] m, int W, int H, float cx, float cy, float rx, float ry)
        {
            int x0 = Math.Max(0, (int)Math.Floor(cx - rx - 1)), x1 = Math.Min(W - 1, (int)Math.Ceiling(cx + rx + 1));
            int y0 = Math.Max(0, (int)Math.Floor(cy - ry - 1)), y1 = Math.Min(H - 1, (int)Math.Ceiling(cy + ry + 1));
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) { float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry; if (dx * dx + dy * dy <= 1f) m[y * W + x] = true; }
        }
        static void Segment(bool[] m, int W, int H, float ax, float ay, float bx, float by, int thick)
        {
            float r = Math.Max(0.5f, thick * 0.5f);
            int x0 = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - r - 1)), x1 = Math.Min(W - 1, (int)Math.Ceiling(Math.Max(ax, bx) + r + 1));
            int y0 = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - r - 1)), y1 = Math.Min(H - 1, (int)Math.Ceiling(Math.Max(ay, by) + r + 1));
            float vx = bx - ax, vy = by - ay; float len2 = Math.Max(1e-6f, vx * vx + vy * vy);
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f; float t = ((px - ax) * vx + (py - ay) * vy) / len2; t = Math.Max(0f, Math.Min(1f, t));
                float dx = px - (ax + t * vx), dy = py - (ay + t * vy); if (dx * dx + dy * dy <= r * r) m[y * W + x] = true;
            }
        }
        /// <summary>Catmull-Rom 으로 제어점 사이를 segs 개씩 보간</summary>
        static List<float[]> Spline(float[][] pts, int segs)
        {
            var o = new List<float[]>();
            for (int i = 0; i < pts.Length - 1; i++)
            {
                var p0 = pts[Math.Max(0, i - 1)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(pts.Length - 1, i + 2)];
                for (int s = 0; s < segs; s++)
                {
                    float t = (float)s / segs, t2 = t * t, t3 = t2 * t;
                    float x = 0.5f * (2 * p1[0] + (-p0[0] + p2[0]) * t + (2 * p0[0] - 5 * p1[0] + 4 * p2[0] - p3[0]) * t2 + (-p0[0] + 3 * p1[0] - 3 * p2[0] + p3[0]) * t3);
                    float y = 0.5f * (2 * p1[1] + (-p0[1] + p2[1]) * t + (2 * p0[1] - 5 * p1[1] + 4 * p2[1] - p3[1]) * t2 + (-p0[1] + 3 * p1[1] - 3 * p2[1] + p3[1]) * t3);
                    o.Add(new[] { x, y });
                }
            }
            o.Add(pts[pts.Length - 1]);
            return o;
        }
        /// <summary>짝홀 규칙 스캔라인 채우기</summary>
        static void FillPolygon(bool[] m, int W, int H, List<float[]> poly)
        {
            int n = poly.Count; if (n < 3) return;
            float ymin = float.MaxValue, ymax = float.MinValue; foreach (var p in poly) { ymin = Math.Min(ymin, p[1]); ymax = Math.Max(ymax, p[1]); }
            var xs = new List<float>();
            for (int y = Math.Max(0, (int)Math.Floor(ymin)); y <= Math.Min(H - 1, (int)Math.Ceiling(ymax)); y++)
            {
                float sy = y + 0.5f; xs.Clear();
                for (int i = 0; i < n; i++)
                {
                    var a = poly[i]; var b = poly[(i + 1) % n];
                    if ((a[1] <= sy && b[1] > sy) || (b[1] <= sy && a[1] > sy)) xs.Add(a[0] + (sy - a[1]) * (b[0] - a[0]) / (b[1] - a[1]));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int x0 = Math.Max(0, (int)Math.Round(xs[k])), x1 = Math.Min(W - 1, (int)Math.Round(xs[k + 1]) - 1);
                    for (int x = x0; x <= x1; x++) m[y * W + x] = true;
                }
            }
        }
    }
}
