// PainterlyCore.cs — 회화풍 모드의 텍스처 처리 (2026-10-10, 기록 Q 절). 유니티에 의존하지 않는 순수 계산.
//   1) 이방성 쿠와하라 (Kyprianidis, Kang, Döllner 2009): 구조 텐서로 방향·이방성을 구해, 방향을 맞춘 타원을 8부채꼴로 나누고
//      분산이 작은 부채꼴의 평균을 강하게 택한다 → 가장자리는 남고 안쪽은 붓으로 민 색면이 된다.
//   2) 흐름을 따르는 붓털 줄무늬: 접선 방향으로 선적분 합성곱(LIC)한 노이즈로 밝기를 변조. 줄무늬 폭은 노이즈를 미리 흐려서 정한다.
//      (쿠와하라만으로는 손그림 텍스처가 그냥 흐려진다 — The Boss 관문 시험 1차. 붓 느낌은 이 줄무늬에서 온다.)
//   3) 마무리: 패치 가장자리 강조(언샤프), 종이 결, 약한 채도 감소, 약한 대비.
// 값은 The Boss 1024² 에서 사용자가 고른 '넓은 붓(r9)' 열을 기준으로 하고, 다른 텍스처에서는 기준 길이(눈 사이 거리의 20%, 텍셀)에 비례시킨다.
using System;
using System.Threading.Tasks;

namespace FBX2NPR.EditorTools
{
    public static class PainterlyCore
    {
        public class Opt
        {
            public float brushFrac = 1.1f;        // 쿠와하라 반경 = brushFrac × hwTex (The Boss: hwTex 8.2 → 9)
            public int brushMin = 4, brushMax = 16;
            public float q = 8f;                  // 부채꼴 가중치 지수
            // 부채꼴 가중치 1/(1+(σ·s0)^q). 30 미만이면 손그림 텍스처에서 그냥 블러가 되고, 60 이면 사진 피부(σ 0.01~0.03)의 미세한 차이에 부채꼴이 뒤바뀌어
            // 얼룩덜룩한 대리석 무늬가 된다(Ch07 실측). 30 이 둘 사이.
            public float s0 = 30f;
            public float sigmaFrac = 0.43f;       // 구조 텐서 흐림 = sigmaFrac × 반경 (r14 → 6). 작으면 평평한 피부에서 방향이 잡음이라 줄무늬가 얼룩이 된다
            public float streakLenFrac = 1.55f;   // 줄무늬 길이 = streakLenFrac × 반경 (r9 → 14)
            public float streakWidthFrac = 0.45f; // 줄무늬 노이즈 흐림 = streakWidthFrac × 반경 (r9 → 4: 폭 3~6텍셀)
            public float streakAmp = 0.20f;       // 줄무늬 밝기 변조
            public float sharpen = 0.4f;          // 패치 가장자리 강조 (0.7 은 사진 피부의 패치 경계까지 세워 얼룩이 됐다)
            public float grain = 5f;              // 종이 결 (8비트 단위)
            public float desat = 0.92f;           // 채도 (1 = 그대로)
            public float contrast = 1.05f;
            // 이보다 큰 텍스처는 절반 해상도에서 붓질하고 올린다(시간). 2048 을 절반에서 하면 사진 피부(Ch07)가 전체 해상도보다 훨씬 얼룩졌다(실측, 원인 미확인) —
            // 2048 은 전체 해상도로 13초쯤 걸리므로 그대로 하고, 4096 부터만 절반으로.
            public int halfResAbove = 4096;
        }

        public class Result { public int radius, streakLen, streakWidth; public bool halfRes; public double seconds; }

        public static Result Run(byte[] rgba, int W, int H, float hwTex, Opt o) { return Run(rgba, W, H, hwTex, o, null, null); }

        /// <summary>rgba(8비트, 알파는 그대로 두고 RGB 만 처리)를 제자리에서 회화풍으로 바꾼다.</summary>
        /// <param name="smooth">텍셀별 0~1 (null 가능): 1 이면 얼굴 피부 — 붓털 줄무늬와 가장자리 강조를 끄고 붓질 결과를 한 번 더 부드럽게 (사용자 지시 2026-10-10)</param>
        /// <param name="protect">텍셀별 0~1 (null 가능): 1 이면 원본 그대로 (안구 등 또렷해야 하는 것)</param>
        public static Result Run(byte[] rgba, int W, int H, float hwTex, Opt o, float[] smooth, float[] protect)
        {
            var res = new Result(); var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] original = protect != null ? (byte[])rgba.Clone() : null;
            int r = Math.Max(o.brushMin, Math.Min(o.brushMax, (int)Math.Round(o.brushFrac * hwTex)));
            bool half = Math.Max(W, H) > o.halfResAbove && W % 2 == 0 && H % 2 == 0;
            int pw = half ? W / 2 : W, ph = half ? H / 2 : H; int pr = half ? Math.Max(2, r / 2) : r;
            res.radius = r; res.halfRes = half;
            // float RGB (0~1)
            var px = new float[pw * ph * 3];
            if (half)
            {
                for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) for (int c = 0; c < 3; c++)
                {
                    int i0 = ((2 * y) * W + 2 * x) * 4 + c, i1 = i0 + 4, i2 = i0 + W * 4, i3 = i2 + 4;
                    px[(y * pw + x) * 3 + c] = (rgba[i0] + rgba[i1] + rgba[i2] + rgba[i3]) / (4f * 255f);
                }
            }
            else for (int i = 0; i < W * H; i++) for (int c = 0; c < 3; c++) px[i * 3 + c] = rgba[i * 4 + c] / 255f;

            float[] phi, aniso;
            var k = Kuwahara(px, pw, ph, pr, o.q, o.s0, Math.Max(1f, o.sigmaFrac * pr), out phi, out aniso);
            int len = Math.Max(2, (int)Math.Round(o.streakLenFrac * pr)), width = Math.Max(1, (int)Math.Round(o.streakWidthFrac * pr));
            res.streakLen = len; res.streakWidth = width;
            // 얼굴 피부: 붓질 결과(패치)를 반경의 절반으로 한 번 더 흐려 패치 경계를 녹이고, 줄무늬는 끈다
            float[] sm = smooth;
            if (sm != null && half) { sm = new float[pw * ph]; for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) sm[y * pw + x] = smooth[(2 * y) * W + 2 * x]; }
            if (sm != null)
            {
                var kb = new float[k.Length]; var ch = new float[pw * ph];
                for (int c = 0; c < 3; c++) { for (int i = 0; i < pw * ph; i++) ch[i] = k[i * 3 + c]; var g = Gauss(ch, pw, ph, Math.Max(1.0, 0.5 * pr)); for (int i = 0; i < pw * ph; i++) kb[i * 3 + c] = g[i]; }
                for (int i = 0; i < pw * ph; i++) { float s = sm[i]; if (s <= 0f) continue; for (int c = 0; c < 3; c++) k[i * 3 + c] += (kb[i * 3 + c] - k[i * 3 + c]) * s; }
            }
            if (o.streakAmp > 0f) k = Streaks(k, pw, ph, phi, aniso, len, o.streakAmp, width, 11, sm);
            // 전체 해상도로 (쌍선형) 되돌린 뒤 마무리
            float[] full;
            if (half)
            {
                full = new float[W * H * 3];
                for (int y = 0; y < H; y++)
                {
                    float sy = (y + 0.5f) / 2f - 0.5f; int y0 = Math.Max(0, Math.Min(ph - 1, (int)Math.Floor(sy))), y1 = Math.Min(ph - 1, y0 + 1); float fy = Math.Max(0f, Math.Min(1f, sy - y0));
                    for (int x = 0; x < W; x++)
                    {
                        float sx = (x + 0.5f) / 2f - 0.5f; int x0 = Math.Max(0, Math.Min(pw - 1, (int)Math.Floor(sx))), x1 = Math.Min(pw - 1, x0 + 1); float fx = Math.Max(0f, Math.Min(1f, sx - x0));
                        for (int c = 0; c < 3; c++)
                        {
                            float a = k[(y0 * pw + x0) * 3 + c], b = k[(y0 * pw + x1) * 3 + c], cc = k[(y1 * pw + x0) * 3 + c], d = k[(y1 * pw + x1) * 3 + c];
                            full[(y * W + x) * 3 + c] = (a + (b - a) * fx) + ((cc + (d - cc) * fx) - (a + (b - a) * fx)) * fy;
                        }
                    }
                }
            }
            else full = k;
            var f = Finish(full, W, H, o.sharpen, o.grain, o.desat, o.contrast, 7, smooth);
            for (int i = 0; i < W * H; i++) for (int c = 0; c < 3; c++) rgba[i * 4 + c] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(f[i * 3 + c] * 255f)));
            // 또렷해야 하는 곳(안구)은 원본으로 되돌린다
            if (protect != null && original != null)
                for (int i = 0; i < W * H; i++) { float p = protect[i]; if (p <= 0f) continue; for (int c = 0; c < 3; c++) rgba[i * 4 + c] = (byte)Math.Round(rgba[i * 4 + c] + (original[i * 4 + c] - rgba[i * 4 + c]) * p); }
            sw.Stop(); res.seconds = sw.Elapsed.TotalSeconds;
            return res;
        }

        /// <summary>0~1 마스크를 가우시안으로 부드럽게 (경계 띠가 안 생기게). 바깥 코드용.</summary>
        public static float[] BlurMask(float[] m, int W, int H, double sigma) { return Gauss(m, W, H, sigma); }

        static float Lum(float[] p, int i) { return 0.2126f * p[i * 3] + 0.7152f * p[i * 3 + 1] + 0.0722f * p[i * 3 + 2]; }

        static float[] Gauss(float[] s, int W, int H, double sigma)
        {
            int r = Math.Max(1, (int)Math.Ceiling(sigma * 3)); var k = new float[2 * r + 1]; float sum = 0;
            for (int i = -r; i <= r; i++) { k[i + r] = (float)Math.Exp(-i * i / (2 * sigma * sigma)); sum += k[i + r]; }
            for (int i = 0; i < k.Length; i++) k[i] /= sum;
            var t = new float[W * H]; var o = new float[W * H];
            Parallel.For(0, H, y => { for (int x = 0; x < W; x++) { float a = 0; for (int i = -r; i <= r; i++) { int xx = Math.Min(W - 1, Math.Max(0, x + i)); a += s[y * W + xx] * k[i + r]; } t[y * W + x] = a; } });
            Parallel.For(0, W, x => { for (int y = 0; y < H; y++) { float a = 0; for (int i = -r; i <= r; i++) { int yy = Math.Min(H - 1, Math.Max(0, y + i)); a += t[yy * W + x] * k[i + r]; } o[y * W + x] = a; } });
            return o;
        }

        static float[] Kuwahara(float[] px, int W, int H, int R, double q, double s0, double sigma, out float[] phiOut, out float[] anisoOut)
        {
            int P = W * H, N = 8; var E = new float[P]; var F = new float[P]; var G = new float[P];
            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    int xm = Math.Max(0, x - 1), xp = Math.Min(W - 1, x + 1), ym = Math.Max(0, y - 1), yp = Math.Min(H - 1, y + 1);
                    float gx = 0, gy = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        float a = px[(ym * W + xm) * 3 + c], b = px[(ym * W + x) * 3 + c], cc = px[(ym * W + xp) * 3 + c], d = px[(y * W + xm) * 3 + c], f = px[(y * W + xp) * 3 + c], g = px[(yp * W + xm) * 3 + c], hh = px[(yp * W + x) * 3 + c], ii = px[(yp * W + xp) * 3 + c];
                        gx += (cc + 2 * f + ii) - (a + 2 * d + g); gy += (g + 2 * hh + ii) - (a + 2 * b + cc);
                    }
                    gx /= 12f; gy /= 12f; int i = y * W + x; E[i] = gx * gx; F[i] = gx * gy; G[i] = gy * gy;
                }
            });
            E = Gauss(E, W, H, sigma); F = Gauss(F, W, H, sigma); G = Gauss(G, W, H, sigma);
            var phi = new float[P]; var A = new float[P];
            Parallel.For(0, P, i =>
            {
                double e = E[i], f = F[i], g = G[i]; double disc = Math.Sqrt((e - g) * (e - g) + 4 * f * f); double l1 = (e + g + disc) / 2, l2 = (e + g - disc) / 2;
                double tx = l1 - e, ty = -f; if (Math.Abs(tx) < 1e-12 && Math.Abs(ty) < 1e-12) { tx = 1; ty = 0; }
                phi[i] = (float)Math.Atan2(ty, tx); A[i] = (l1 + l2) > 1e-12 ? (float)((l1 - l2) / (l1 + l2)) : 0f;
            });
            phiOut = phi; anisoOut = A;
            var o = new float[P * 3];
            Parallel.For(0, H, y =>
            {
                var mR = new double[N]; var mG = new double[N]; var mB = new double[N]; var sq = new double[N]; var wsum = new double[N];
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x; double a = R * (1.0 + A[i]), b = R / (1.0 + A[i]); double cs = Math.Cos(phi[i]), sn = Math.Sin(phi[i]);
                    int rx = (int)Math.Ceiling(Math.Max(a * Math.Abs(cs), b * Math.Abs(sn))), ry = (int)Math.Ceiling(Math.Max(a * Math.Abs(sn), b * Math.Abs(cs)));
                    for (int k = 0; k < N; k++) { mR[k] = mG[k] = mB[k] = sq[k] = wsum[k] = 0; }
                    for (int dy = -ry; dy <= ry; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= H) continue;
                        for (int dx = -rx; dx <= rx; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= W) continue;
                            double u = (dx * cs + dy * sn) / a, v = (-dx * sn + dy * cs) / b; double rr = u * u + v * v; if (rr > 1.0) continue;
                            double ang = Math.Atan2(v, u); int k = (int)Math.Floor((ang + Math.PI) / (2 * Math.PI) * N); if (k >= N) k = N - 1; if (k < 0) k = 0;
                            double w = Math.Exp(-2.5 * rr); int j = (yy * W + xx) * 3; double lr = px[j], lg = px[j + 1], lb = px[j + 2];
                            mR[k] += w * lr; mG[k] += w * lg; mB[k] += w * lb; double L = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb; sq[k] += w * L * L; wsum[k] += w;
                        }
                    }
                    double oR = 0, oG = 0, oB = 0, ow = 0;
                    for (int k = 0; k < N; k++)
                    {
                        if (wsum[k] < 1e-9) continue;
                        double rr_ = mR[k] / wsum[k], gg = mG[k] / wsum[k], bb = mB[k] / wsum[k]; double L = 0.2126 * rr_ + 0.7152 * gg + 0.0722 * bb;
                        double var_ = Math.Max(0, sq[k] / wsum[k] - L * L); double s = Math.Sqrt(var_);
                        double al = 1.0 / (1.0 + Math.Pow(s * s0, q)); oR += al * rr_; oG += al * gg; oB += al * bb; ow += al;
                    }
                    if (ow > 1e-12) { o[i * 3] = (float)(oR / ow); o[i * 3 + 1] = (float)(oG / ow); o[i * 3 + 2] = (float)(oB / ow); }
                    else { o[i * 3] = px[i * 3]; o[i * 3 + 1] = px[i * 3 + 1]; o[i * 3 + 2] = px[i * 3 + 2]; }
                }
            });
            return o;
        }

        static float[] Streaks(float[] px, int W, int H, float[] phi, float[] aniso, int len, double amp, int width, int seed, float[] smooth)
        {
            int P = W * H; var rnd = new Random(seed); var n = new float[P]; for (int i = 0; i < P; i++) n[i] = (float)rnd.NextDouble();
            if (width > 1) n = Gauss(n, W, H, width * 0.5);
            var lic = new float[P];
            Parallel.For(0, H, y =>
            {
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x; double cs = Math.Cos(phi[i]), sn = Math.Sin(phi[i]); double acc = 0; int cnt = 0;
                    for (int s = -len; s <= len; s++) { int xx = (int)Math.Round(x + s * cs), yy = (int)Math.Round(y + s * sn); if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue; acc += n[yy * W + xx]; cnt++; }
                    lic[i] = cnt > 0 ? (float)(acc / cnt) : 0.5f;
                }
            });
            double mu = 0; for (int i = 0; i < P; i++) mu += lic[i]; mu /= P;
            double var2 = 0; for (int i = 0; i < P; i++) var2 += (lic[i] - mu) * (lic[i] - mu); double sd = Math.Sqrt(var2 / P) + 1e-6;
            var o = new float[P * 3];
            for (int i = 0; i < P; i++)
            {
                float m = (float)(1.0 + amp * ((lic[i] - mu) / sd) * 0.5); float a = aniso[i];
                // 구조가 없는 곳(이방성 < 0.15)은 줄무늬를 아예 끈다. 방향이 잡음인 곳에 줄무늬를 넣으면 평평한 피부가 대리석처럼 얼룩진다(Ch07 실측)
                float ga = (a - 0.15f) / 0.35f; ga = Math.Max(0f, Math.Min(1f, ga)); ga = ga * ga * (3f - 2f * ga);
                if (smooth != null) ga *= 1f - smooth[i];   // 얼굴 피부에는 줄무늬를 넣지 않는다
                m = 1f + (m - 1f) * ga;
                for (int c = 0; c < 3; c++) o[i * 3 + c] = Math.Max(0f, Math.Min(1f, px[i * 3 + c] * m));
            }
            return o;
        }

        static float[] Finish(float[] px, int W, int H, double sharpen, double grain, double desat, double contrast, int seed, float[] smooth)
        {
            int P = W * H; var L = new float[P]; for (int i = 0; i < P; i++) L[i] = Lum(px, i); var Lb = Gauss(L, W, H, 2.0);
            var rnd = new Random(seed); var n = new float[P]; for (int i = 0; i < P; i++) n[i] = (float)(rnd.NextDouble() - 0.5); var nb = Gauss(n, W, H, 0.8);
            var o = new float[P * 3];
            for (int i = 0; i < P; i++)
            {
                float keep = smooth != null ? 1f - smooth[i] : 1f;   // 얼굴 피부에는 가장자리 강조를 끄고 종이 결은 절반만
                float dl = (float)(sharpen * (L[i] - Lb[i])) * keep; float gn = (float)(grain / 255.0) * nb[i] * 3f * (0.5f + 0.5f * keep); float lum = L[i];
                for (int c = 0; c < 3; c++)
                {
                    float v = px[i * 3 + c]; v = (float)(lum + (v - lum) * desat); v = (float)(0.5 + (v - 0.5) * contrast); v += dl + gn;
                    o[i * 3 + c] = Math.Max(0f, Math.Min(1f, v));
                }
            }
            return o;
        }
    }
}
