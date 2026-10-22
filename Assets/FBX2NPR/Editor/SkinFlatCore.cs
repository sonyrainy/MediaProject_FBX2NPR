// SkinFlatCore.cs
// 얼굴 피부만 평평한 색으로 만들고 이목구비(눈썹·아이라인·입술·수염 등)는 남깁니다.
// 유니티에 의존하지 않는 순수 C# 이라 텍스처 조각으로 오프라인 검증을 할 수 있습니다. (C# 5 문법만 사용)
//
// 원리:
//   1) 피부 표본에서 평평한 피부색(flat)을 구합니다.
//   2) 텍셀마다 "주변 피부(국소 밑색)보다 얼마나 어둡거나 색이 다른가"를 잽니다.
//      모공·홍조·완만한 구운 명암은 주변과 차이가 작고, 눈썹·입술·아이라인은 큽니다.
//      (국소 기준은 폭이 국소 반경보다 좁은 무늬에만 듣습니다. 넓은 면은 4번의 전역 기준으로 갈립니다.)
//   3) 차이가 큰 '강' 텍셀을 씨앗으로, 거기 붙은 '약' 텍셀을 조금만 덧붙여 이목구비 마스크를 만듭니다.
//      (약한 것만 넓게 퍼진 구운 명암은 씨앗이 없어 통째로 평탄화됩니다.)
//   4) 평탄색에서 아예 멀리 벗어난 면(머리 밑칠, 수염, 눈구멍, 흰자)도 남깁니다.
//   5) 마스크 밖 피부는 flat 한 색으로 칠합니다.
//
// 한계(4체 실험으로 확인): 피부가 한 색이라는 가정 위에 서 있습니다. 피부색이 둘 이상이거나(도마뱀 인간 Jones)
// 넓고 옅은 그늘이 캐릭터의 특징인 경우(The Boss 의 수염 그늘)는 깨집니다.
using System;
using System.Collections.Generic;

namespace FBX2NPR
{
    public static class SkinFlatCore
    {
        public class Opt
        {
            // 기본값은 텍스처 조각 오프라인 시험(Ch07·The Boss)으로 맞췄습니다. 임계값은 전부 sRGB 8비트 절대 차이입니다.
            //   18/30 → 눈 둘레·콧방울의 구운 그늘이 얼룩으로 남음, 28/45 → 눈썹·아이라인만 남음
            //   전역 70/90 → The Boss 수염(밝기 차 약 69)이 절반만 남음, 50/65 → 띠로 남음
            public float tL0 = 28f, tL1 = 45f;      // 국소 밑색보다 어두운 정도(Y): 약 / 강
            public float tC0 = 8f, tC1 = 14f;       // 국소 밑색과의 색차(R-G, B-G 평면 거리): 약 / 강
            public float tG0 = 50f, tG1 = 65f;      // 피부 중앙색에서 전역 이탈(어두움 또는 색차): 약 / 강
            public float tBright = 40f;             // 피부 중앙색보다 밝은 면(흰자·이빨·반창고): 이 값이면 약, +15 면 강
            public bool excludeGlobal = true;       // 국소 밑색을 구할 때 전역으로 어두운 면(수염·눈구멍)을 빼서, 넓은 어두운 면이 주변 밑색을 끌어내리지 않게 함
            public float radiusFrac = 0.6f;         // 국소 반경 = radiusFrac * hwTex
            public float growFrac = 0.05f;          // 씨앗에서 '약' 텍셀로 번지는 거리 = growFrac * hwTex (0.12 는 눈 둘레에 테가 남음)
            public float minSizeFrac = 0.6f;        // 남길 성분의 최소 넓이 = (minSizeFrac * hwTex)². 가늘고 긴 선은 긴 변이 그 두 배 이상이면 남김
            public float minEdge = 5f;              // 성분 가장자리와 바로 바깥 피부의 평균 밝기 차 하한. 홍조·붓질 얼룩처럼 경계가 완만한 것은 떨어지고 눈썹·입술은 남음. 0 이면 검사 안 함
            public bool feather = true;             // 남긴 성분 바깥을 부드럽게
            // 부드럽게 넘기는 폭 = featherFrac * hwTex 텍셀 (1~4 로 자름). 바깥 한 줄(1텍셀)만 흐리면 남긴 무늬의 가장자리가 텍셀 단위로
            // 들쭉날쭉해, 가까이서 보면 눈·입술·콧구멍 둘레가 계단처럼 보입니다(사용자 지적). 남긴 텍셀을 그 폭만큼 넓힌 뒤 같은 폭으로 흐려,
            // 남긴 텍셀 위에서는 1 이고 바깥으로 폭의 두 배에 걸쳐 0 이 되게 합니다. 흐림이 그 폭보다 작은 들쭉날쭉함을 고릅니다.
            public float featherFrac = 0.08f;
            public float featFlat = 0f;             // 남긴 성분을 성분 평균색으로 단순화(0 원본 ~ 1 단색). 4체 시험에서는 켜면 더 나빴음
            // 평탄색을 뽑는 밝기 백분위 구간. 구운 명암은 피부를 어둡게만 하므로 '그늘이 없는 피부색'은 밝은 쪽에 있습니다.
            // 0.25~0.75(중앙값)는 The Boss 가 원본의 밝은 면보다 채널당 40~50 어두운 적갈색이 됐고, 0.60~0.95 에서 원본의 밝은 면에 가까워졌습니다.
            public float flatLo = 0.60f, flatHi = 0.95f;
            public float gate = 0.45f;              // 피부 잔무늬(gateFine)가 이보다 작으면 이미 평평하게 칠한 텍스처로 보고 건너뜀
            public float gateRadiusFrac = 0.05f;    // 참고값(gateFineScaled)을 재는 반경 = max(1, gateRadiusFrac * hwTex) 텍셀. 판정은 반경 1 로 잰 값으로 함
            public bool force;                      // 게이트 무시
            // 칠할 색을 밖에서 정해 줍니다(판정 기준은 그대로 이 영역의 중앙색). 목·손을 얼굴과 같은 평탄색으로 칠할 때 씁니다.
            public bool useFill; public byte fillR, fillG, fillB;
            // 라벨 경로용(텍셀별, 호출하는 쪽이 자른 크기에 맞춰 넣음): 국소 대비로 '남길' 수 있는 텍셀만 true. null 이면 전부 허용(예전 동작).
            // 파서가 '피부'라고 한 곳에서 대비 규칙까지 켜 두면 예전처럼 구운 그늘이 얼룩으로 남습니다. 코 라벨 안(콧구멍)만 허용하는 데 씁니다.
            public bool[] allowKeep;
        }

        public class Result
        {
            public bool skipped; public string note = "";
            public byte flatR, flatG, flatB;        // 평평한 피부색
            public byte medR, medG, medB;           // 표본 전체 중앙색(참고)
            public float tgtR, tgtG, tgtB;          // 평탄화 대상 영역의 원래 평균색(참고, 변환 도구의 색 검사용)
            public float gateG;                     // 표본 밝기의 전체 흩어짐 1.4826 * MAD(Y) (참고용, 판정에는 안 씀)
            public float gateLocal, gateFine;       // 국소 반경 수준 잔차(참고), 바로 옆 텍셀 수준 잔무늬(반경 1, 판정에 씀)
            public float gateFineScaled;            // 반경을 기준 길이에 맞춰 키워 잰 잔무늬(참고)
            public float otsuEta, otsuGap, otsuLow; // 피부 밝기가 두 무리로 갈리는 정도(η), 두 무리 평균 차, 어두운 무리 비율
            public double sampleW; public int sampleN;
            public int radius, grow, minArea, gateRadius, featherRadius;
            public int targetN, keptN, comps;       // 대상 텍셀(w>=0.5), 남긴 텍셀, 남긴 성분 수
            public float skinPct, keepPct;
            public byte[] outRgba;                  // 결과 텍스처 (건너뛰면 null)
            // 등급 마스크: 빨강 = 평탄화한 피부, 초록 = 남긴 이목구비, 노랑 = 대상이지만 건너뛰어 손대지 않음,
            //             하늘(0,128,255) = 가중치 0~0.5 인 전이 구간(가중치만큼 섞임), 파랑 = 대상 아님
            public byte[] maskRgba;
            public float[] F;                       // 유지 비중 0~1 (건너뛰면 null)
            public List<string> info = new List<string>(); // 성분별 기록(넓이, 위치, 경계 기울기, 평균색) — 임계값 맞출 때 봅니다
        }

        static int HistPct(double[] hist, double p)
        {
            double tot = 0; for (int i = 0; i < hist.Length; i++) tot += hist[i];
            if (tot <= 0) return 0;
            double acc = 0, target = p * tot;
            for (int i = 0; i < hist.Length; i++) { acc += hist[i]; if (acc >= target) return i; }
            return hist.Length - 1;
        }

        // 정규화한 박스 블러 (바깥은 0). src -> dst, tmp 는 작업용.
        static void Box(float[] src, float[] dst, float[] tmp, int W, int H, int r)
        {
            double inv = 1.0 / (2 * r + 1);
            for (int y = 0; y < H; y++)
            {
                int row = y * W; double acc = 0;
                for (int x = 0; x <= r && x < W; x++) acc += src[row + x];
                for (int x = 0; x < W; x++)
                {
                    tmp[row + x] = (float)(acc * inv);
                    int xo = x - r, xn = x + r + 1;
                    if (xo >= 0) acc -= src[row + xo];
                    if (xn < W) acc += src[row + xn];
                }
            }
            for (int x = 0; x < W; x++)
            {
                double acc = 0;
                for (int y = 0; y <= r && y < H; y++) acc += tmp[y * W + x];
                for (int y = 0; y < H; y++)
                {
                    dst[y * W + x] = (float)(acc * inv);
                    int yo = y - r, yn = y + r + 1;
                    if (yo >= 0) acc -= tmp[yo * W + x];
                    if (yn < H) acc += tmp[yn * W + x];
                }
            }
        }

        // 마스크 안 값만으로 3회 박스 블러. 가중합이 없는 곳은 fill.
        static float[] MaskedBlur3(float[] val, bool[] ok, int W, int H, int r, float fill, float[] den3)
        {
            int N = W * H; var num = new float[N]; var a = new float[N]; var tmp = new float[N];
            for (int i = 0; i < N; i++) num[i] = ok[i] ? val[i] : 0f;
            Box(num, a, tmp, W, H, r); Box(a, num, tmp, W, H, r); Box(num, a, tmp, W, H, r);
            for (int i = 0; i < N; i++) a[i] = den3[i] > 1e-4f ? a[i] / den3[i] : fill;
            return a;
        }
        static float[] MaskDen3(bool[] ok, int W, int H, int r)
        {
            int N = W * H; var d = new float[N]; var a = new float[N]; var tmp = new float[N];
            for (int i = 0; i < N; i++) d[i] = ok[i] ? 1f : 0f;
            Box(d, a, tmp, W, H, r); Box(a, d, tmp, W, H, r); Box(d, a, tmp, W, H, r);
            return a;
        }

        /// <param name="rgba">sRGB 8비트 RGBA, 길이 W*H*4 (바꾸지 않습니다)</param>
        /// <param name="wt">텍셀별 얼굴 가중치 0~1 (0 이면 손대지 않음. 0~1 사이는 그만큼만 섞습니다)</param>
        /// <param name="sampleW">피부 표본 가중치 (0 이면 표본 아님)</param>
        /// <param name="cov">이 텍스처를 쓰는 어떤 메시든 덮는 텍셀인지 (null 이면 전부 덮임). 덮이지 않은 여백만 번짐으로 채웁니다</param>
        /// <param name="hwTex">기준 길이(텍셀). 사람 눈 반폭쯤 — 호출하는 쪽이 눈 사이 거리의 20% 로 줍니다</param>
        /// <param name="keepGeo">지오메트리로 강제 유지할 비중 0~1 (null 가능)</param>
        /// <param name="killGeo">지오메트리로 강제 평탄화할 비중 0~1 (null 가능)</param>
        public static Result Run(byte[] rgba, int W, int H, float[] wt, float[] sampleW, bool[] cov, float hwTex, float[] keepGeo, float[] killGeo, Opt o)
        {
            int N = W * H;
            var res = new Result();
            var Y = new float[N]; var U = new float[N]; var V = new float[N];
            for (int i = 0; i < N; i++)
            {
                int r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
                Y[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b; U[i] = r - g; V[i] = b - g;
            }

            // ---- 등급 마스크. 대상은 일단 노랑(손대지 않음)으로 두고, 실제로 처리한 뒤에 빨강/초록으로 바꿉니다.
            //      건너뛴 텍스처의 마스크가 '평탄화했다'로 읽히지 않게 하기 위해서입니다.
            res.maskRgba = new byte[N * 4];
            for (int i = 0; i < N; i++)
            {
                bool covered = cov == null || cov[i];
                bool target = covered && wt[i] >= 0.5f, ramp = covered && wt[i] > 0f && wt[i] < 0.5f;
                if (target) res.targetN++;
                int m = i * 4;
                if (target) { res.maskRgba[m] = 255; res.maskRgba[m + 1] = 255; res.maskRgba[m + 2] = 0; }
                else if (ramp) { res.maskRgba[m] = 0; res.maskRgba[m + 1] = 128; res.maskRgba[m + 2] = 255; }
                else { res.maskRgba[m] = 0; res.maskRgba[m + 1] = 0; res.maskRgba[m + 2] = 255; }
                res.maskRgba[m + 3] = 255;
            }

            // ---- 피부 표본 통계
            var hY = new double[256];
            for (int i = 0; i < N; i++) if (sampleW[i] > 0f) { hY[(int)Math.Min(255f, Y[i])] += sampleW[i]; res.sampleN++; }
            if (res.sampleN < 500) { res.skipped = true; res.note = "표본 없음"; return res; }
            int medY0 = HistPct(hY, 0.5);
            float darkCut = 0.5f * medY0;             // 머리 밑칠 같은 검정은 표본에서 뺍니다
            Array.Clear(hY, 0, 256);
            var hR = new double[256]; var hG = new double[256]; var hB = new double[256];
            for (int i = 0; i < N; i++)
            {
                if (sampleW[i] <= 0f || Y[i] < darkCut) continue;
                hY[(int)Math.Min(255f, Y[i])] += sampleW[i]; res.sampleW += sampleW[i];
                hR[rgba[i * 4]] += sampleW[i]; hG[rgba[i * 4 + 1]] += sampleW[i]; hB[rgba[i * 4 + 2]] += sampleW[i];
            }
            int medY = HistPct(hY, 0.5);
            res.medR = (byte)HistPct(hR, 0.5); res.medG = (byte)HistPct(hG, 0.5); res.medB = (byte)HistPct(hB, 0.5);
            // 평탄화 대상 영역 전체의 원래 평균색 (머리 밑칠 같은 검정은 뺌). 변환 도구가 평탄색과 견주어
            // '피부가 한 색이 아닌 텍스처'(머리 전체의 색이 바뀌는 경우)를 가려내는 데 씁니다. 판정에는 쓰지 않습니다.
            {
                double tR = 0, tG = 0, tB = 0; long tn = 0;
                for (int i = 0; i < N; i++)
                {
                    if (wt[i] < 0.5f || (cov != null && !cov[i]) || Y[i] < darkCut) continue;
                    tR += rgba[i * 4]; tG += rgba[i * 4 + 1]; tB += rgba[i * 4 + 2]; tn++;
                }
                if (tn > 0) { res.tgtR = (float)(tR / tn); res.tgtG = (float)(tG / tn); res.tgtB = (float)(tB / tn); }
            }
            var hD = new double[256];
            for (int k = 0; k < 256; k++) hD[Math.Abs(k - medY)] += hY[k];
            res.gateG = 1.4826f * HistPct(hD, 0.5);
            // 두 무리로 갈리는 정도 (Otsu)
            {
                double tot = 0, sum = 0; for (int k = 0; k < 256; k++) { tot += hY[k]; sum += k * hY[k]; }
                double mu = sum / Math.Max(tot, 1e-9), vr = 0; for (int k = 0; k < 256; k++) vr += (k - mu) * (k - mu) * hY[k]; vr /= Math.Max(tot, 1e-9);
                double w0 = 0, s0 = 0, best = 0, bGap = 0, bLow = 0;
                for (int k = 0; k < 255; k++)
                {
                    w0 += hY[k]; s0 += k * hY[k]; double w1 = tot - w0; if (w0 <= 0 || w1 <= 0) continue;
                    double m0 = s0 / w0, m1 = (sum - s0) / w1, bv = (w0 / tot) * (w1 / tot) * (m1 - m0) * (m1 - m0);
                    if (bv > best) { best = bv; bGap = m1 - m0; bLow = w0 / tot; }
                }
                res.otsuEta = vr > 1e-6 ? (float)(best / vr) : 0f; res.otsuGap = (float)bGap; res.otsuLow = (float)bLow;
            }
            // 평탄색: 밝기 flatLo~flatHi 백분위 구간의 채널별 중앙값 (구운 그늘을 피하고, 맨 위 하이라이트도 뺌)
            int yLo = HistPct(hY, o.flatLo), yHi = HistPct(hY, o.flatHi);
            Array.Clear(hR, 0, 256); Array.Clear(hG, 0, 256); Array.Clear(hB, 0, 256);
            for (int i = 0; i < N; i++)
            {
                if (sampleW[i] <= 0f || Y[i] < yLo || Y[i] > yHi + 1) continue;
                hR[rgba[i * 4]] += sampleW[i]; hG[rgba[i * 4 + 1]] += sampleW[i]; hB[rgba[i * 4 + 2]] += sampleW[i];
            }
            res.flatR = (byte)HistPct(hR, 0.5); res.flatG = (byte)HistPct(hG, 0.5); res.flatB = (byte)HistPct(hB, 0.5);
            if (o.useFill) { res.flatR = o.fillR; res.flatG = o.fillG; res.flatB = o.fillB; }
            // 판정의 기준색은 평탄색이 아니라 표본 중앙색입니다. 칠하는 색(밝은 쪽)과 판정 기준(가운데)을 나눕니다.
            // 밝은 평탄색을 기준으로 삼으면 '평탄색보다 50 어두움'에 눈 밑의 옅은 그늘까지 걸려 Ch07 눈 밑에 갈색 조각이 남았습니다.
            float Yf = 0.2126f * res.medR + 0.7152f * res.medG + 0.0722f * res.medB, Uf = res.medR - res.medG, Vf = res.medB - res.medG;

            // ---- 국소 밑색 (2회: 1회차에서 이목구비로 보인 텍셀을 빼고 다시)
            int R = Math.Max(3, (int)Math.Round(o.radiusFrac * hwTex));
            int Rcap = Math.Max(3, W / 16); if (R > Rcap) { res.note += "국소 반경 " + R + " -> " + Rcap + " 로 잘림. "; R = Rcap; }
            res.radius = R;
            var ok = new bool[N];
            for (int i = 0; i < N; i++) ok[i] = wt[i] > 0f && (cov == null || cov[i]) && Y[i] >= 0.5f * Yf;
            float[] bY = null, bU = null, bV = null;
            float midL = 0.5f * (o.tL0 + o.tL1), midC = 0.5f * (o.tC0 + o.tC1);
            for (int pass = 0; pass < 2; pass++)
            {
                var den = MaskDen3(ok, W, H, R);
                bY = MaskedBlur3(Y, ok, W, H, R, Yf, den); bU = MaskedBlur3(U, ok, W, H, R, Uf, den); bV = MaskedBlur3(V, ok, W, H, R, Vf, den);
                if (pass == 0)
                {
                    // 게이트: 표본 피부에 '잔무늬'(모공 크기 수준의 주변과의 차이)가 있는가. 사진의 모공·노이즈와 붓질 얼룩은 있고,
                    // 손으로 평평하게 칠한 알베도는 넓은 그라데이션이 있어도 잔무늬는 0 에 가깝습니다.
                    // 전체 밝기 흩어짐(gateG)이나 국소 반경 수준의 잔차(gateLocal)로 판정하면 안 됩니다. 유니티짱 얼굴 가장자리의 살구색
                    // 그라데이션이 표본 창에 들어오면 둘 다 Ch07 만큼 커져서(g 13.3, 국소 3.3 대 3.7), 건드리면 안 되는 텍스처를 구워 버립니다.
                    // (유니티 안 대조군에서 실제로 그랬습니다.)
                    // 주의: 압축된 임포트 텍스처에서 읽으면 압축 잡음이 값을 키웁니다(The Boss 무압축 2.3 → 임포트 4.2). 문턱은 4체로만 정했습니다.
                    // 판정은 반경 1텍셀로 잰 값으로 합니다. 반경을 기준 길이에 맞춰 키운 값(gateFineScaled)도 같이 남기지만 판정에는 쓰지 않습니다.
                    // 키운 반경에서는 손그림의 완만한 그라데이션이 잔차에 섞여 값이 올라가고(유니티짱 0.00 → 0.29), 그 방향의 오판은
                    // '손으로 칠한 텍스처를 구워 버리는' 되돌리기 어려운 쪽입니다. 반경 1 의 오판(고해상도 사진을 건너뜀)은 아무것도 안 하는 안전한 쪽입니다.
                    int rG = Math.Max(1, (int)Math.Round(o.gateRadiusFrac * hwTex)); res.gateRadius = rG;
                    var hRes = new double[1024]; var hFine = new double[4096]; var hScaled = new double[4096]; double wOk = 0;
                    var den1 = MaskDen3(ok, W, H, 1); var fY = MaskedBlur3(Y, ok, W, H, 1, Yf, den1);
                    float[] sY = fY; if (rG > 1) { var denS = MaskDen3(ok, W, H, rG); sY = MaskedBlur3(Y, ok, W, H, rG, Yf, denS); }
                    for (int i = 0; i < N; i++)
                    {
                        if (sampleW[i] <= 0f || !ok[i]) continue;
                        wOk += sampleW[i];
                        hRes[(int)Math.Min(1023f, Math.Abs(Y[i] - bY[i]) * 4f)] += sampleW[i];
                        hFine[(int)Math.Min(4095f, Math.Abs(Y[i] - fY[i]) * 64f)] += sampleW[i];
                        hScaled[(int)Math.Min(4095f, Math.Abs(Y[i] - sY[i]) * 64f)] += sampleW[i];
                    }
                    if (wOk <= 0) { res.skipped = true; res.note += "표본이 대상 텍셀과 겹치지 않음"; return res; }
                    res.gateLocal = 1.4826f * HistPct(hRes, 0.5) / 4f;
                    res.gateFine = 1.4826f * (HistPct(hFine, 0.5) + 0.5f) / 64f; res.gateFineScaled = 1.4826f * (HistPct(hScaled, 0.5) + 0.5f) / 64f;
                    // 문턱의 1.5배 안쪽(위아래 모두)이면 판정이 불확실하다고 남깁니다. 문턱은 4체로만 정했습니다.
                    if (res.gateFine >= o.gate / 1.5f && res.gateFine < o.gate * 1.5f) res.note += "잔무늬가 문턱 근처(판정 불확실). ";
                    if (res.gateFine < o.gate)
                    {
                        if (!o.force) { res.skipped = true; res.note += "이미 평평함"; return res; }
                        res.note += "문턱 미달이지만 강제 적용. ";
                    }

                    for (int i = 0; i < N; i++)
                    {
                        if (!ok[i]) continue;
                        float du = U[i] - bU[i], dv = V[i] - bV[i];
                        bool feat = bY[i] - Y[i] >= midL || Math.Sqrt(du * du + dv * dv) >= midC;
                        if (!feat && o.excludeGlobal)
                        {
                            float gu = U[i] - Uf, gv = V[i] - Vf;
                            feat = Math.Max(Yf - Y[i], (float)Math.Sqrt(gu * gu + gv * gv)) >= o.tG0;
                        }
                        if (feat) ok[i] = false;
                    }
                }
            }

            // ---- 강 / 약 판정
            var valid = new bool[N]; var strong = new bool[N]; var weak = new bool[N];
            for (int i = 0; i < N; i++)
            {
                if (wt[i] <= 0f || (cov != null && !cov[i])) continue;
                valid[i] = true;
                if (o.allowKeep != null && !o.allowKeep[i]) continue;   // 라벨이 피부라고 한 곳: 무조건 평탄화
                float dL = bY[i] - Y[i];
                float du = U[i] - bU[i], dv = V[i] - bV[i]; float dC = (float)Math.Sqrt(du * du + dv * dv);
                float gu = U[i] - Uf, gv = V[i] - Vf; float gd = Math.Max(Yf - Y[i], (float)Math.Sqrt(gu * gu + gv * gv));
                float gb = Y[i] - Yf;
                strong[i] = dL >= o.tL1 || dC >= o.tC1 || gd >= o.tG1 || gb >= o.tBright + 15f;
                weak[i] = strong[i] || dL >= o.tL0 || dC >= o.tC0 || gd >= o.tG0 || gb >= o.tBright;
            }

            // ---- 씨앗(강)에서 '약' 텍셀로 제한 거리만큼 번짐
            int D = Math.Max(1, (int)Math.Round(o.growFrac * hwTex)); res.grow = D;
            var kept = new bool[N]; var dist = new int[N]; var queue = new int[N]; int qh = 0, qt = 0;
            for (int i = 0; i < N; i++) { dist[i] = -1; if (strong[i]) { kept[i] = true; dist[i] = 0; queue[qt++] = i; } }
            while (qh < qt)
            {
                int p = queue[qh++]; if (dist[p] >= D) continue;
                int x = p % W, y = p / W;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue; int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                    int q = yy * W + xx; if (dist[q] >= 0 || !weak[q]) continue;
                    dist[q] = dist[p] + 1; kept[q] = true; queue[qt++] = q;
                }
            }

            // ---- 성분 정리: 너무 작은 것(모공·주근깨), 경계가 완만한 것(구운 그늘) 제거. 성분 평균색도 구합니다.
            float minSide = o.minSizeFrac * hwTex;
            int minArea = Math.Max(4, (int)Math.Round(minSide * minSide)); res.minArea = minArea;
            var label = new int[N]; var compMeanR = new List<float>(); var compMeanG = new List<float>(); var compMeanB = new List<float>();
            compMeanR.Add(0); compMeanG.Add(0); compMeanB.Add(0);
            var stack = queue; var members = new List<int>();
            for (int s = 0; s < N; s++)
            {
                if (!kept[s] || label[s] != 0) continue;
                int id = compMeanR.Count; members.Clear(); int sp = 0; stack[sp++] = s; label[s] = id;
                double sr = 0, sg = 0, sb = 0, edgeSum = 0; int edgeN = 0; int bx0 = W, by0 = H, bx1 = -1, by1 = -1;
                while (sp > 0)
                {
                    int p = stack[--sp]; members.Add(p); int x = p % W, y = p / W;
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y;
                    sr += rgba[p * 4]; sg += rgba[p * 4 + 1]; sb += rgba[p * 4 + 2];
                    bool border = false;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue; int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        int q = yy * W + xx;
                        if (!kept[q]) { if (dx == 0 || dy == 0) border = true; continue; }
                        if (label[q] == 0) { label[q] = id; stack[sp++] = q; }
                    }
                    // 경계의 또렷함: 성분 가장자리 텍셀과 바로 바깥 피부 텍셀의 밝기 차(가장 큰 것).
                    // 바깥 이웃은 얼굴 텍셀(valid)만 봅니다. UV 섬 가장자리의 여백이나 다른 섬과의 밝기 차가 섞이면 안 됩니다.
                    // 성분 텍셀 자신의 중앙차분으로 재면 폭 1텍셀짜리 선은 양쪽이 둘 다 피부라 0 이 나와 버려집니다(합성 시험으로 확인).
                    if (border)
                    {
                        float mxd = -1f;
                        if (x > 0 && !kept[p - 1] && valid[p - 1]) mxd = Math.Max(mxd, Math.Abs(Y[p] - Y[p - 1]));
                        if (x < W - 1 && !kept[p + 1] && valid[p + 1]) mxd = Math.Max(mxd, Math.Abs(Y[p] - Y[p + 1]));
                        if (y > 0 && !kept[p - W] && valid[p - W]) mxd = Math.Max(mxd, Math.Abs(Y[p] - Y[p - W]));
                        if (y < H - 1 && !kept[p + W] && valid[p + W]) mxd = Math.Max(mxd, Math.Abs(Y[p] - Y[p + W]));
                        if (mxd >= 0f) { edgeSum += mxd; edgeN++; }
                    }
                }
                int area = members.Count; int longSide = Math.Max(bx1 - bx0, by1 - by0) + 1;
                // 넓이가 모자라도 가늘고 긴 선(아이라인 꼬리, 쌍꺼풀 선)은 남깁니다.
                bool bigEnough = area >= minArea || (longSide >= 2f * minSide && area >= Math.Max(4f, minSide));
                bool drop = !bigEnough;
                float edgeMean = edgeN > 0 ? (float)(edgeSum / edgeN) : 0f;
                if (!drop && o.minEdge > 0f)
                {
                    if (edgeN > 0) { if (edgeMean < o.minEdge) drop = true; }
                    // 바깥 피부와 맞닿은 곳이 없는 성분(UV 섬 전체가 어두운 입 안·눈구멍 테)은 검사 없이 남기되, 넓이 기준은 채워야 합니다.
                    // 가늘고 긴 조각까지 봐주면 UV 섬 가장자리에 스민 어두운 테가 무조건 남습니다.
                    else if (area < minArea) drop = true;
                }
                if (bigEnough && res.info.Count < 400)
                {
                    long cx = 0, cy = 0; for (int k = 0; k < members.Count; k++) { cx += members[k] % W; cy += members[k] / W; }
                    res.info.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0,8} {1} at ({2:F2},{3:F2}) edge {4:F1} mean ({5:F0},{6:F0},{7:F0})",
                        area, drop ? "버림" : "남김", (double)cx / area / W, (double)cy / area / H, edgeMean, sr / area, sg / area, sb / area));
                }
                if (drop) { for (int k = 0; k < members.Count; k++) kept[members[k]] = false; compMeanR.Add(0); compMeanG.Add(0); compMeanB.Add(0); }
                else { compMeanR.Add((float)(sr / area)); compMeanG.Add((float)(sg / area)); compMeanB.Add((float)(sb / area)); res.comps++; }
            }

            // 지오메트리·라벨로 강제 유지하는 텍셀도 남긴 텍셀과 같이 흐림의 출발점이 됩니다. 아니면 입술·눈썹 라벨의 가장자리가 텍셀 계단으로 끊깁니다.
            if (keepGeo != null) for (int i = 0; i < N; i++) if (keepGeo[i] > 0.5f && valid[i]) kept[i] = true;
            // ---- 마스크 → 유지 비중 F. 남긴 텍셀은 1 로 두고 바깥 한 줄만 부드럽게 합니다(가는 선의 대비를 깎지 않게).
            var F = new float[N];
            for (int i = 0; i < N; i++) F[i] = kept[i] ? 1f : 0f;
            if (o.feather)
            {
                int fr = Math.Max(1, Math.Min(4, (int)Math.Round(o.featherFrac * hwTex)));
                res.featherRadius = fr;
                var a = new float[N]; var tmp = new float[N];
                if (fr <= 1) { Box(F, a, tmp, W, H, 1); for (int i = 0; i < N; i++) if (!kept[i]) F[i] = a[i]; }
                else
                {
                    // 넓히기(가로·세로 최댓값) → 같은 반경으로 흐림 → 부드러운 곡선으로
                    var d = new float[N];
                    for (int y = 0; y < H; y++)
                    {
                        int row = y * W;
                        for (int x = 0; x < W; x++)
                        {
                            float mx = 0f; int x0 = Math.Max(0, x - fr), x1 = Math.Min(W - 1, x + fr);
                            for (int xx = x0; xx <= x1; xx++) if (F[row + xx] > mx) { mx = F[row + xx]; if (mx >= 1f) break; }
                            tmp[row + x] = mx;
                        }
                    }
                    for (int x = 0; x < W; x++)
                        for (int y = 0; y < H; y++)
                        {
                            float mx = 0f; int y0 = Math.Max(0, y - fr), y1 = Math.Min(H - 1, y + fr);
                            for (int yy = y0; yy <= y1; yy++) if (tmp[yy * W + x] > mx) { mx = tmp[yy * W + x]; if (mx >= 1f) break; }
                            d[y * W + x] = mx;
                        }
                    Box(d, a, tmp, W, H, fr);
                    for (int i = 0; i < N; i++) if (!kept[i]) { float v = Math.Max(0f, Math.Min(1f, a[i])); F[i] = v * v * (3f - 2f * v); }
                }
            }
            for (int i = 0; i < N; i++)
            {
                if (killGeo != null) F[i] *= 1f - killGeo[i];
                if (keepGeo != null) F[i] = Math.Max(F[i], keepGeo[i]);
            }
            res.F = F;

            // ---- 합성
            var outp = new byte[N * 4];
            for (int i = 0; i < N; i++)
            {
                int o4 = i * 4; float w = wt[i]; if (cov != null && !cov[i]) w = 0f;
                float fr = rgba[o4], fg = rgba[o4 + 1], fb = rgba[o4 + 2];
                if (o.featFlat > 0f && label[i] != 0 && kept[i])
                {
                    fr += (compMeanR[label[i]] - fr) * o.featFlat; fg += (compMeanG[label[i]] - fg) * o.featFlat; fb += (compMeanB[label[i]] - fb) * o.featFlat;
                }
                float f = F[i];
                float tr = res.flatR + (fr - res.flatR) * f, tg = res.flatG + (fg - res.flatG) * f, tb = res.flatB + (fb - res.flatB) * f;
                outp[o4] = (byte)Math.Round(rgba[o4] + (tr - rgba[o4]) * w);
                outp[o4 + 1] = (byte)Math.Round(rgba[o4 + 1] + (tg - rgba[o4 + 1]) * w);
                outp[o4 + 2] = (byte)Math.Round(rgba[o4 + 2] + (tb - rgba[o4 + 2]) * w);
                outp[o4 + 3] = rgba[o4 + 3];
                bool target = wt[i] >= 0.5f && (cov == null || cov[i]);
                if (target)
                {
                    bool keepPx = (kept[i] && f > 0.5f) || (keepGeo != null && keepGeo[i] > 0.5f); if (keepPx) res.keptN++;   // 바깥 흐림 줄은 '남김'으로 세지 않습니다
                    res.maskRgba[o4] = keepPx ? (byte)0 : (byte)255; res.maskRgba[o4 + 1] = keepPx ? (byte)255 : (byte)0; res.maskRgba[o4 + 2] = 0;
                }
            }
            // ---- 덮이지 않은 여백으로 번짐 (밉·쌍선형 샘플이 섬 밖 원본을 끌어오지 않게). cov 가 다른 메시의 섬까지 포함해야 그 섬을 덮지 않습니다.
            if (cov != null)
            {
                var faceSrc = new bool[N];                             // 번짐의 출처가 될 수 있는 텍셀(얼굴 섬과 이미 번진 텍셀)
                for (int i = 0; i < N; i++) faceSrc[i] = cov[i] && wt[i] > 0f;
                for (int it = 0; it < 4; it++)
                {
                    var add = new List<int>();
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x; if (cov[i] || faceSrc[i]) continue;
                        int src = -1;
                        if (x > 0 && faceSrc[i - 1]) src = i - 1; else if (x < W - 1 && faceSrc[i + 1]) src = i + 1;
                        else if (y > 0 && faceSrc[i - W]) src = i - W; else if (y < H - 1 && faceSrc[i + W]) src = i + W;
                        if (src < 0) continue;                         // 얼굴 섬 가장자리에서만 번집니다
                        add.Add(i); add.Add(src);
                    }
                    for (int k = 0; k < add.Count; k += 2)
                    {
                        int i = add[k], s = add[k + 1];
                        outp[i * 4] = outp[s * 4]; outp[i * 4 + 1] = outp[s * 4 + 1]; outp[i * 4 + 2] = outp[s * 4 + 2];
                    }
                    for (int k = 0; k < add.Count; k += 2) faceSrc[add[k]] = true;
                }
            }
            res.outRgba = outp;
            res.skinPct = res.targetN > 0 ? 100f * (res.targetN - res.keptN) / res.targetN : 0f;
            res.keepPct = res.targetN > 0 ? 100f * res.keptN / res.targetN : 0f;
            return res;
        }
    }
}
