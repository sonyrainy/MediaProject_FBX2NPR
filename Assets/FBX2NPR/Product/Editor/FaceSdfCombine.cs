using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 빛 각도별 흑백 마스크를 SDF 맵 채널 하나로 합친다. 맵의 값은 "빛이 이 각도까지 돌아가는 동안 이 칸은 밝다"이다.
    /// 마스크 사이 각도는 경계까지의 거리로 나눠 잇는다: 앞 마스크 경계에서 a 칸, 뒤 마스크 경계에서 b 칸 떨어진 칸은 두 각도를 a : b 로 나눈 곳에서 어두워진다.
    /// 그래서 마스크를 5도 간격으로만 구워도 그 사이 각도에서 경계가 매끄럽게 움직이고, 사람이 고친 마스크도 앞뒤 각도와 이어진다.
    /// 한 칸은 한 번만 어두워진다고 본다. 처음부터 어두운 칸은 끝까지 어둡게, 어두워졌다가 다시 밝아지는 칸은 처음 어두워지는 각도로 담는다.
    /// Unity 에 기대지 않는 계산이라 테스트로 바로 돌릴 수 있다.
    /// </summary>
    public static class FaceSdfCombine
    {
        /// <summary>값 0 과 1 은 "처음부터 그늘", "끝까지 밝음"에 쓰고, 각도는 그 안쪽에 담는다. FaceSDF.hlsl 의 FACE_SDF_MARGIN 과 같은 값.</summary>
        public const float Margin = 2f / 255f;
        public const float MaxDeg = 180f;

        public static float Encode(float deg) { return Margin + (1f - 2f * Margin) * Mathf.Clamp01(deg / MaxDeg); }
        public static float Decode(float v) { return (v - Margin) / (1f - 2f * Margin) * MaxDeg; }

        /// <summary>칸마다 가장 가까운 덮인 칸의 번호 (덮인 칸은 자기 번호). 덮인 칸이 없으면 -1. 앞뒤 두 번 훑어 이웃의 답을 물려받는다.</summary>
        public static int[] NearestCovered(bool[] covered, int W, int H)
        {
            var near = new int[W * H];
            for (int i = 0; i < near.Length; i++) near[i] = covered[i] ? i : -1;
            Func<int, int, int, long> dist = (x, y, n) => { if (n < 0) return long.MaxValue; long dx = x - n % W, dy = y - n / W; return dx * dx + dy * dy; };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x; long best = dist(x, y, near[i]);
                    if (x > 0) { int n = near[i - 1]; long d = dist(x, y, n); if (d < best) { best = d; near[i] = n; } }
                    if (y > 0)
                    {
                        int n = near[i - W]; long d = dist(x, y, n); if (d < best) { best = d; near[i] = n; }
                        if (x > 0) { n = near[i - W - 1]; d = dist(x, y, n); if (d < best) { best = d; near[i] = n; } }
                        if (x + 1 < W) { n = near[i - W + 1]; d = dist(x, y, n); if (d < best) { best = d; near[i] = n; } }
                    }
                }
            for (int y = H - 1; y >= 0; y--)
                for (int x = W - 1; x >= 0; x--)
                {
                    int i = y * W + x; long best = dist(x, y, near[i]);
                    if (x + 1 < W) { int n = near[i + 1]; long d = dist(x, y, n); if (d < best) { best = d; near[i] = n; } }
                    if (y + 1 < H)
                    {
                        int n = near[i + W]; long d = dist(x, y, n); if (d < best) { best = d; near[i] = n; }
                        if (x + 1 < W) { n = near[i + W + 1]; d = dist(x, y, n); if (d < best) { best = d; near[i] = n; } }
                        if (x > 0) { n = near[i + W - 1]; d = dist(x, y, n); if (d < best) { best = d; near[i] = n; } }
                    }
                }
            return near;
        }

        // 1차원 거리 변환 (Felzenszwalb & Huttenlocher). f 의 하계 포물선들로 제곱 거리를 구한다.
        static void Dt1(double[] f, int n, double[] d, int[] v, double[] z)
        {
            int k = 0; v[0] = 0; z[0] = double.NegativeInfinity; z[1] = double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double s = ((f[q] + q * q) - (f[v[k]] + v[k] * (double)v[k])) / (2.0 * q - 2.0 * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + q * q) - (f[v[k]] + v[k] * (double)v[k])) / (2.0 * q - 2.0 * v[k]);
                }
                k++; v[k] = q; z[k] = s; z[k + 1] = double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        // 제곱 거리. g 는 대상 칸 0, 나머지 아주 큰 값으로 넣는다. 정확한 유클리드 거리다.
        static void Edt(double[] g, int W, int H)
        {
            Parallel.For(0, W, x =>
            {
                var f = new double[H]; var d = new double[H]; var v = new int[H]; var z = new double[H + 1];
                for (int y = 0; y < H; y++) f[y] = g[y * W + x];
                Dt1(f, H, d, v, z);
                for (int y = 0; y < H; y++) g[y * W + x] = d[y];
            });
            Parallel.For(0, H, y =>
            {
                var f = new double[W]; var d = new double[W]; var v = new int[W]; var z = new double[W + 1];
                Array.Copy(g, y * W, f, 0, W);
                Dt1(f, W, d, v, z);
                Array.Copy(d, 0, g, y * W, W);
            });
        }

        const double Far = 1e12;

        /// <summary>경계까지의 부호 있는 거리 (칸 단위). inside 인 칸은 양수, 아닌 칸은 음수, 경계를 사이에 둔 두 칸은 ±0.5.</summary>
        public static float[] SignedDistance(bool[] inside, int W, int H)
        {
            int N = W * H;
            var toOut = new double[N]; var toIn = new double[N];
            for (int i = 0; i < N; i++) { toOut[i] = inside[i] ? Far : 0.0; toIn[i] = inside[i] ? 0.0 : Far; }
            Edt(toOut, W, H); Edt(toIn, W, H);
            var d = new float[N];
            for (int i = 0; i < N; i++) d[i] = inside[i] ? (float)(Math.Sqrt(toOut[i]) - 0.5) : -(float)(Math.Sqrt(toIn[i]) - 0.5);
            return d;
        }

        /// <summary>
        /// 마스크를 합쳐 칸마다 맵 값을 만든다. lit[i] 는 각도 deg[i] 에서 밝은 칸이고, deg 는 오름차순이다.
        /// covered 밖의 칸은 가장 가까운 덮인 칸의 값을 받는다. 얼굴 UV 조각의 가장자리가 그늘 경계로 잡히지 않고, 샘플링이 바깥으로 번져도 값이 튀지 않는다.
        /// </summary>
        public static float[] Thresholds(IList<bool[]> lit, IList<float> deg, bool[] covered, int W, int H)
        {
            if (lit.Count != deg.Count || lit.Count == 0) throw new ArgumentException("마스크와 각도 수가 다릅니다");
            int N = W * H;
            var near = NearestCovered(covered, W, H);
            var t = new float[N]; var done = new bool[N];
            var prev = Filled(lit[0], near); var dPrev = SignedDistance(prev, W, H);
            for (int i = 0; i < N; i++) if (!prev[i]) { t[i] = 0f; done[i] = true; }
            for (int m = 1; m < lit.Count; m++)
            {
                var cur = Filled(lit[m], near); var dCur = SignedDistance(cur, W, H);
                for (int i = 0; i < N; i++)
                {
                    if (done[i] || cur[i]) continue;
                    float a = dPrev[i], b = dCur[i];
                    float f = a <= 0f ? 0f : a / Mathf.Max(a - b, 1e-6f);
                    t[i] = Encode(deg[m - 1] + f * (deg[m] - deg[m - 1])); done[i] = true;
                }
                prev = cur; dPrev = dCur;
            }
            for (int i = 0; i < N; i++) if (!done[i]) t[i] = 1f;
            return t;
        }

        static bool[] Filled(bool[] mask, int[] near)
        {
            var o = new bool[mask.Length];
            for (int i = 0; i < o.Length; i++) o[i] = near[i] >= 0 && mask[near[i]];
            return o;
        }
    }
}
