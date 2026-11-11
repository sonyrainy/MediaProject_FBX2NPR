using System.Collections.Generic;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 얼굴 SDF 맵(FaceSdfBake)을 lilToon 의 SDF 그림자 마스크 형식으로 바꾸는 계산 (lilToon 1.9.0 이상, 影設定 マスクタイプ = SDF).
    /// Unity 에셋을 다루지 않아 테스트로 바로 돌릴 수 있다. 파일과 재질은 FaceSdfLilToonExport 가 만든다.
    ///
    /// lilToon 2.4.1 lil_common_frag.hlsl 의 SDF 분기: v = saturate(0.5·cosθ + 0.5·s + 0.25) 가 _ShadowBorder(b) 보다 크면 밝음.
    /// Blend Y(_ShadowFlatBlur)를 0.001 로 두면 θ 는 우리 맵처럼 빛의 수평 각이 되므로, 우리 각도 θt 는 s = 2b − 0.5 − cosθt 로 옮긴다.
    /// 채널: R = 물체 −X 쪽(캐릭터 왼쪽) 빛, G = +X 쪽 빛, B = 보통 그림자와 섞는 비율, A = 그늘 세기. 우리 맵은 R 이 오른쪽(+X) 빛이라 바꿔 넣는다.
    /// s 가 0~1 이라 재질 하나는 빛 각도 [θmin, θmax] 만 담고, 그 밖의 각도는 잘린다 (b = 0.5 → 60°~120°).
    /// B 는 1 − 얼굴 가중치라 몸처럼 얼굴 명암을 쓰지 않는 곳은 lilToon 의 보통 그림자(N·L)가 된다.
    /// A 는 양쪽 빛 모두 끝까지 밝은 칸(눈 등)에서만 1 − 얼굴 가중치로 낮춘다 (얼굴이면 0, 그늘이 지지 않는다).
    /// lilToon 마스크에는 코 채널이 없어서 코 그림자는 큰 명암선에 합친다 (MergeNose).
    /// </summary>
    public static class FaceSdfLilToon
    {
        /// <summary>
        /// 내보낼 때 쓰는 경계: lilToon 기본값 그대로다 (빛 60°~120°).
        /// 재질마다 경계를 고르면 한 얼굴에 빛 범위가 여럿 생겨 재질 경계에서 어긋나고(Galaxia 66°~127°·47°~109°·23°~95°),
        /// 같은 재질을 쓰는 몸의 N·L 경계도 함께 바뀌었다 (2026-11-11 리뷰). 얼굴 재질에서 고른 값도 0.45~0.59 라 기본값과 가까웠다.
        /// </summary>
        public const float Border = 0.5f;

        /// <summary>우리 맵 값 → 그 칸이 어두워지기 시작하는 빛의 수평 각도(도). 0 = 처음부터 그늘(0°), 255 = 끝까지 밝음(180°): 여백 밖이라 잘린다.</summary>
        public static float AngleOf(byte t)
        {
            return Mathf.Clamp(FaceSdfCombine.Decode(t / 255f), 0f, FaceSdfCombine.MaxDeg);
        }

        /// <summary>각도 θt 에서 어두워지는 칸의 lilToon 마스크 값 s (0~1 로 잘림).</summary>
        public static float LilValue(float thetaDeg, float border)
        {
            return Mathf.Clamp01(2f * border - 0.5f - Mathf.Cos(thetaDeg * Mathf.Deg2Rad));
        }

        /// <summary>경계 b 에서 담기는 각도 범위: s = 0 칸이 어두워지는 각 ~ s = 1 칸이 어두워지는 각.</summary>
        public static void Window(float border, out float minDeg, out float maxDeg)
        {
            minDeg = Mathf.Acos(Mathf.Clamp(2f * border - 0.5f, -1f, 1f)) * Mathf.Rad2Deg;
            maxDeg = Mathf.Acos(Mathf.Clamp(2f * border - 1.5f, -1f, 1f)) * Mathf.Rad2Deg;
        }

        /// <summary>코 그림자(B·A)를 큰 명암선(R·G)에 합친다: 칸마다 먼저 어두워지는 쪽. 값이 각도 순서라 작은 값을 고르면 된다.</summary>
        public static Color32[] MergeNose(Color32[] ours)
        {
            var o = new Color32[ours.Length];
            for (int i = 0; i < ours.Length; i++)
            {
                var p = ours[i];
                o[i] = new Color32(p.r < p.b ? p.r : p.b, p.g < p.a ? p.g : p.a, 255, 255);
            }
            return o;
        }

        /// <summary>
        /// 마스크 크기: 우리 맵(얼굴 UV 사각형 rect 만 담음)과 같은 칸 밀도로 UV 0~1 전체를 펼치되, 긴 변이 maxSize 를 넘으면 같은 비율로 줄인다.
        /// 가로·세로는 4의 배수다 (블록 압축).
        /// </summary>
        public static void OutputSize(int w, int h, Rect rect, int maxSize, out int outW, out int outH)
        {
            float sw = w / rect.width, sh = h / rect.height, k = Mathf.Min(1f, maxSize / Mathf.Max(sw, sh));
            outW = Round4(sw * k); outH = Round4(sh * k);
        }

        /// <summary>
        /// 우리 맵(R·G = 오른쪽·왼쪽 빛) → lilToon 마스크(UV 0~1 전체, outW×outH, 경계 Border). 각도를 쌍선형으로 읽은 뒤 s 로 바꾼다.
        /// s 는 각도에 비선형이라 값을 먼저 바꿔 섞으면 경계가 어긋난다. faceWeight 는 칸마다 얼굴 가중치다 (FaceWeightMap).
        /// </summary>
        public static Color32[] Convert(Color32[] ours, int w, int h, Rect rect, float[] faceWeight, int outW, int outH)
        {
            var right = new float[ours.Length]; var left = new float[ours.Length];
            for (int i = 0; i < ours.Length; i++) { right[i] = AngleOf(ours[i].r); left[i] = AngleOf(ours[i].g); }
            var res = new Color32[outW * outH];
            for (int j = 0; j < outH; j++)
                for (int i = 0; i < outW; i++)
                {
                    float thR, thL;
                    Sample(right, left, w, h, rect, new Vector2((i + 0.5f) / outW, (j + 0.5f) / outH), out thR, out thL);
                    float fw = Mathf.Clamp01(faceWeight[j * outW + i]);
                    bool alwaysLit = thR >= AlwaysLitDeg && thL >= AlwaysLitDeg;
                    res[j * outW + i] = new Color32(Byte(LilValue(thL, Border)), Byte(LilValue(thR, Border)), Byte(1f - fw), alwaysLit ? Byte(1f - fw) : (byte)255);
                }
            return res;
        }

        // 이 각도 이상이면 끝까지 밝은 칸으로 본다 (쌍선형으로 읽어 180° 에서 조금 모자랄 수 있다)
        const float AlwaysLitDeg = 179.5f;

        // 우리 맵을 UV 로 쌍선형으로 읽어 오른쪽·왼쪽 빛 각도를 낸다. 사각형 밖은 가장자리 값
        static void Sample(float[] right, float[] left, int w, int h, Rect rect, Vector2 uv, out float thR, out float thL)
        {
            float fx = (uv.x - rect.x) / rect.width * w - 0.5f, fy = (uv.y - rect.y) / rect.height * h - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, w - 1), y0 = Mathf.Clamp(Mathf.FloorToInt(fy), 0, h - 1);
            int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
            thR = Mathf.Lerp(Mathf.Lerp(right[y0 * w + x0], right[y0 * w + x1], tx), Mathf.Lerp(right[y1 * w + x0], right[y1 * w + x1], tx), ty);
            thL = Mathf.Lerp(Mathf.Lerp(left[y0 * w + x0], left[y0 * w + x1], tx), Mathf.Lerp(left[y1 * w + x0], left[y1 * w + x1], tx), ty);
        }

        /// <summary>
        /// 삼각형(uv, 꼭짓점 얼굴 가중치)을 outW×outH 칸에 그려 칸마다 얼굴 가중치를 낸다. 칸 중심이 삼각형 안이면 꼭짓점 값을 잇고, 겹치면 큰 쪽. 밖은 0.
        /// 가장자리는 한 칸 넓힌다(이웃 칸의 큰 값): lilToon 이 쌍선형으로 읽을 때 얼굴 칸에 보통 그림자가 섞여 이음매가 생기지 않게.
        /// </summary>
        public static float[] FaceWeightMap(IList<Vector2> uv, IList<float> weight, IList<int> tris, int outW, int outH)
        {
            var m = new float[outW * outH]; var hit = new bool[outW * outH];
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                Vector2 a = uv[ia], b = uv[ib], c = uv[ic];
                float area = Edge(a, b, c);
                if (Mathf.Abs(area) < 1e-12f) continue;
                int i0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)) * outW - 0.5f)), i1 = Mathf.Min(outW - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x)) * outW - 0.5f));
                int j0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)) * outH - 0.5f)), j1 = Mathf.Min(outH - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y)) * outH - 0.5f));
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        var p = new Vector2((i + 0.5f) / outW, (j + 0.5f) / outH);
                        float la = Edge(b, c, p) / area, lb = Edge(c, a, p) / area, lc = 1f - la - lb;
                        if (la < -1e-6f || lb < -1e-6f || lc < -1e-6f) continue;
                        float v = Mathf.Clamp01(la * weight[ia] + lb * weight[ib] + lc * weight[ic]);
                        int k = j * outW + i;
                        if (!hit[k] || v > m[k]) m[k] = v;
                        hit[k] = true;
                    }
            }
            var o = new float[m.Length];
            for (int j = 0; j < outH; j++)
                for (int i = 0; i < outW; i++)
                {
                    int k = j * outW + i;
                    if (hit[k]) { o[k] = m[k]; continue; }
                    float v = 0f;
                    for (int dj = -1; dj <= 1; dj++)
                        for (int di = -1; di <= 1; di++)
                        {
                            int x = i + di, y = j + dj;
                            if (x >= 0 && y >= 0 && x < outW && y < outH && hit[y * outW + x]) v = Mathf.Max(v, m[y * outW + x]);
                        }
                    o[k] = v;
                }
            return o;
        }

        static float Edge(Vector2 a, Vector2 b, Vector2 p) { return (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x); }

        static int Round4(float x) { return Mathf.Max(4, Mathf.RoundToInt(x / 4f) * 4); }
        static byte Byte(float s) { return (byte)Mathf.RoundToInt(Mathf.Clamp01(s) * 255f); }
    }
}
