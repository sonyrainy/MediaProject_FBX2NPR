// RegionFlatCore.cs
// 얼굴 밖(목·손·팔, 옷·모자)의 텍스처를 얼굴과 같은 수준으로 단순화합니다: 색 무리마다 평평한 색면 + 또렷한 형태선.
// SkinFlatCore(한 가지 색의 면을 평탄화하고 또렷한 무늬는 남김)를 '메시마다, 색 무리마다' 되풀이해 부릅니다.
// 유니티에 의존하지 않는 순수 C# 입니다 (C# 5 문법만 사용).
//
// 순서 (메시 하나에 대해):
//   1) 아직 처리하지 않은 텍셀의 색도(밝기를 뺀 색: r = R/(R+G+B), g = G/(R+G+B)) 히스토그램에서 가장 큰 봉우리를 찾습니다.
//   2) 그 봉우리에서 색도 거리 chromaTol 안의 텍셀이 한 '색 무리'입니다 (같은 천의 밝은 면과 구운 그늘이 한 무리가 됩니다).
//   3) 그 무리를 SkinFlatCore 에 넘깁니다. 칠할 색은 무리의 밝은 쪽 중앙색(구운 그늘을 뺀 색), 판정 기준은 무리의 중앙색.
//      피부 무리는 얼굴의 평탄색으로 칠합니다 — 턱선에서 색이 끊기지 않게.
//   4) 남은 텍셀로 1)부터 다시. 무리가 너무 작아지면 멈춥니다. 무리에 들지 못한 텍셀(솔기·단추·어두운 선)은 원본 그대로입니다.
//
// 피부 무리의 조건 (전부 맞아야 합니다. 하나만으로는 옷을 피부로 칠하는 사고를 못 막습니다):
//   - 메시가 피부 재질 규칙에 맞고(skinRule), 이름이 옷이 아니고(clothName 아님)
//   - 무리의 색도가 얼굴 평탄색과 skinTol 안이고
//   - 무리의 평균 밝기가 얼굴 평탄색 밝기의 skinYLo ~ skinYHi 배 안
// 한계: 피부와 색·밝기가 비슷한 옷(베이지·살구색 천)이 몸과 같은 재질·같은 메시에 있으면 가를 수 없습니다. The Boss 의 바지·모자는
// 색도로는 피부 범위 안이고 재질 이름이 달라서 걸러집니다. 그런 캐릭터는 모델 설정에서 몸 평탄화를 끄거나 옷 메시 이름 규칙에 넣어야 합니다.
using System;
using System.Collections.Generic;

namespace FBX2NPR
{
    public static class RegionFlatCore
    {
        public class Opt
        {
            public float chromaTol = 0.05f;     // 색 무리의 색도 반경
            public float minFrac = 0.04f;       // 메시 텍셀의 이 비율보다 작은 무리는 처리하지 않음
            public int minTexels = 1500;        // 무리의 최소 텍셀 수
            public int maxClusters = 4;         // 메시당 색 무리 수 상한
            public float skinTol = 0.06f;       // 얼굴 평탄색과의 색도 거리가 이 안이면 피부 후보
            public float skinYLo = 0.45f, skinYHi = 1.6f;   // 피부 후보의 평균 밝기 / 얼굴 평탄색 밝기 의 허용 범위
            // 피부가 메시의 대부분(skinMeshFrac 이상)이면, 피부보다 '붉은 쪽'으로 벗어난 텍셀(손끝·손바닥·팔꿈치의 붉은 칠)도 같은 무리에 넣습니다.
            // 따로 떨어지면 평평한 팔 위에 붉은 조각으로 남습니다(The Boss 로 확인). 붉은 쪽만 넣는 이유: 흰 쪽(속옷·붕대·양말)까지 넣으면 피부색으로 칠해 버립니다.
            public float skinWide = 0.20f; public float skinMeshFrac = 0.5f;
            public float darkY = 28f;           // 이보다 어두운 텍셀은 색도가 불안정해 무리에 넣지 않음 (선·그늘의 가장 어두운 부분)
            public bool skinOnly = false;       // 피부 무리만 처리 (옷은 그대로)
            // 옷에서는 '넓고 어두운 면'과 '밝은 면'을 남기지 않습니다. 같은 색 무리 안의 넓은 어두움은 구운 그늘이고 밝음은 구운 하이라이트입니다.
            // (얼굴과 몸의 피부에서는 수염·머리선·흰 조각을 남기려고 전역 기준을 씁니다. 옷에서는 그 기준이 가랑이·겨드랑이의 구운 그늘을 덩어리로 남겼습니다.)
            public bool dropBroadShade = true;
            // 어두운 천에서는 선도 어둡습니다. 밝기 임계값(절대 차이)을 무리의 평균 밝기에 비례해 줄입니다 (refY 에서 1배).
            public bool relativeContrast = true; public float refY = 150f; public float minContrastScale = 0.25f;
            // 얼굴 밖에서 남길 무늬의 최소 크기 (기준 길이 대비). 얼굴(0.6)보다 크게 잡아 짧은 주름 조각이 얼룩처럼 남지 않게 합니다.
            public float regionMinSizeFrac = 1.2f;
            public SkinFlatCore.Opt flat = new SkinFlatCore.Opt();
        }

        public class MeshInfo
        {
            public int id; public string name = ""; public float hwTex;
            public bool skinRule;       // 재질·메시 이름이 피부 규칙에 맞는가
            public bool clothName;      // 메시 이름이 옷(바지·셔츠·모자 등)인가 — 그러면 피부로 보지 않습니다
            public bool skip;           // 눈·이·머리카락처럼 손대지 않을 메시
            public int changed;         // (결과) 이 메시에서 바뀐 텍셀 수
            public int skinClusters;    // (결과) 피부로 칠한 무리 수
        }

        const int Bins = 96;

        /// <param name="rgba">출발 텍스처 sRGB 8비트 RGBA (바꾸지 않습니다)</param>
        /// <param name="meshId">텍셀을 덮는 메시 번호 (0 = 없음)</param>
        /// <param name="done">이미 처리한 텍셀(얼굴 평탄화가 끝난 곳). 손대지 않습니다</param>
        /// <param name="haveFaceFill">얼굴 평탄색이 있는가. 없으면 피부 무리도 자기 색으로 칠합니다</param>
        /// <param name="mask">null 이 아니면 등급을 씁니다: 빨강 = 평탄화, 초록 = 무리 안에서 남긴 무늬, 회색 = 무리에 들지 못함(원본), 검정 = 대상 아님. 피부 무리는 파랑 성분을 더합니다</param>
        public static byte[] Run(byte[] rgba, int W, int H, byte[] meshId, bool[] done, MeshInfo[] meshes,
                                 bool haveFaceFill, byte fillR, byte fillG, byte fillB, Opt o, List<string> log, byte[] mask)
        {
            int N = W * H;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var outp = (byte[])rgba.Clone();
            var cov = new bool[N]; for (int i = 0; i < N; i++) cov[i] = meshId[i] != 0;
            var cr = new float[N]; var cg = new float[N]; var Yv = new float[N];
            for (int i = 0; i < N; i++)
            {
                float r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2]; float s = r + g + b;
                Yv[i] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                if (s > 1f) { cr[i] = r / s; cg[i] = g / s; } else { cr[i] = 0.3333f; cg[i] = 0.3333f; }
            }
            float fs = fillR + fillG + fillB; float fcr = fs > 1f ? fillR / fs : 0.3333f, fcg = fs > 1f ? fillG / fs : 0.3333f;
            float fillY = 0.2126f * fillR + 0.7152f * fillG + 0.0722f * fillB;
            if (mask != null) for (int i = 0; i < N; i++) { mask[i * 4] = 0; mask[i * 4 + 1] = 0; mask[i * 4 + 2] = 0; mask[i * 4 + 3] = 255; }
            var assigned = new bool[N];

            foreach (var m in meshes)
            {
                m.changed = 0; m.skinClusters = 0;
                if (m.skip) { log.Add("메시 " + m.name + ": 건너뜀(제외 목록)"); continue; }
                int total = 0; int bx0 = W, by0 = H, bx1 = -1, by1 = -1;
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    int i = y * W + x; if (meshId[i] != m.id || (done != null && done[i])) continue;
                    total++; if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y;
                    if (mask != null) { mask[i * 4] = 110; mask[i * 4 + 1] = 110; mask[i * 4 + 2] = 110; }
                }
                if (total < o.minTexels) { log.Add("메시 " + m.name + ": 텍셀 " + total + "개, 너무 작아 건너뜀"); continue; }
                int minCluster = Math.Max(o.minTexels, (int)(o.minFrac * total));

                for (int pass = 0; pass < o.maxClusters; pass++)
                {
                    // ---- 색도 히스토그램의 가장 큰 봉우리
                    var hist = new double[Bins * Bins]; int left = 0, dark = 0;
                    for (int y = by0; y <= by1; y++) for (int x = bx0; x <= bx1; x++)
                    {
                        int i = y * W + x; if (meshId[i] != m.id || assigned[i] || (done != null && done[i])) continue;
                        if (Yv[i] < o.darkY) { dark++; continue; }
                        int br = Math.Min(Bins - 1, (int)(cr[i] * Bins)), bg = Math.Min(Bins - 1, (int)(cg[i] * Bins));
                        hist[bg * Bins + br] += 1.0; left++;
                    }
                    if (left < minCluster)
                    {
                        if (pass == 0) log.Add(string.Format(inv, "메시 {0}: 무리 없음 — 그대로 둠 (텍셀 {1}개 중 밝은 텍셀 {2}개, 색도가 불안정한 어두운 텍셀 {3}개)", m.name, total, left, dark));
                        break;
                    }
                    double best = -1; int bi = 0;
                    for (int g = 1; g < Bins - 1; g++) for (int r = 1; r < Bins - 1; r++)
                    {
                        double s = 0; for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) s += hist[(g + dy) * Bins + r + dx];
                        if (s > best) { best = s; bi = g * Bins + r; }
                    }
                    double sw = 0, sr = 0, sg = 0; int pr = bi % Bins, pg = bi / Bins;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        double h = hist[(pg + dy) * Bins + pr + dx]; sw += h; sr += h * (pr + dx + 0.5) / Bins; sg += h * (pg + dy + 0.5) / Bins;
                    }
                    float mr = (float)(sr / Math.Max(sw, 1e-9)), mg = (float)(sg / Math.Max(sw, 1e-9));

                    // ---- 무리 (색도 반경 chromaTol)
                    float tol2 = o.chromaTol * o.chromaTol; double ySum = 0;
                    var inS = new List<int>();
                    for (int y = by0; y <= by1; y++) for (int x = bx0; x <= bx1; x++)
                    {
                        int i = y * W + x; if (meshId[i] != m.id || assigned[i] || (done != null && done[i]) || Yv[i] < o.darkY) continue;
                        float dr = cr[i] - mr, dg = cg[i] - mg; if (dr * dr + dg * dg > tol2) continue;
                        inS.Add(i); ySum += Yv[i];
                    }
                    if (inS.Count < minCluster)
                    {
                        if (pass == 0) log.Add(string.Format(inv, "메시 {0}: 가장 큰 색 무리가 {1}텍셀로 너무 작음 — 그대로 둠", m.name, inS.Count));
                        break;
                    }
                    float yMean = (float)(ySum / inS.Count);

                    // ---- 피부인가: 재질 규칙 + 옷 이름 아님 + 색도 + 밝기
                    float sdr = mr - fcr, sdg = mg - fcg; float skinDist = (float)Math.Sqrt(sdr * sdr + sdg * sdg);
                    float yRatio = fillY > 1f ? yMean / fillY : 1f;
                    bool chromaOk = skinDist <= o.skinTol, lumOk = yRatio >= o.skinYLo && yRatio <= o.skinYHi;
                    bool skin = m.skinRule && !m.clothName && haveFaceFill && chromaOk && lumOk;
                    string why = "";
                    if (!skin && haveFaceFill && chromaOk)
                        why = !m.skinRule ? " (색은 피부와 가깝지만 피부 재질이 아님)" : m.clothName ? " (색은 피부와 가깝지만 메시 이름이 옷)" : !lumOk ? string.Format(inv, " (색은 피부와 가깝지만 밝기가 얼굴의 {0:F2}배)", yRatio) : "";
                    // 피부가 메시의 대부분이면 붉은 쪽으로 벗어난 텍셀도 같은 무리에
                    int widened = 0;
                    if (skin && inS.Count >= o.skinMeshFrac * total && o.skinWide > o.chromaTol)
                    {
                        float wide2 = o.skinWide * o.skinWide;
                        for (int y = by0; y <= by1; y++) for (int x = bx0; x <= bx1; x++)
                        {
                            int i = y * W + x; if (meshId[i] != m.id || assigned[i] || (done != null && done[i]) || Yv[i] < o.darkY) continue;
                            float dr = cr[i] - mr, dg = cg[i] - mg; float d2 = dr * dr + dg * dg;
                            if (d2 <= tol2 || d2 > wide2 || dr < 0f) continue;      // 이미 든 텍셀, 너무 먼 텍셀, 붉은 쪽이 아닌 텍셀은 뺌
                            inS.Add(i); widened++;
                        }
                    }
                    int cnt = inS.Count;
                    int cx0 = W, cy0 = H, cx1 = -1, cy1 = -1;
                    for (int k = 0; k < cnt; k++)
                    {
                        int i = inS[k]; assigned[i] = true; int x = i % W, y = i / W;
                        if (x < cx0) cx0 = x; if (x > cx1) cx1 = x; if (y < cy0) cy0 = y; if (y > cy1) cy1 = y;
                    }
                    string head = string.Format(inv, "메시 {0} 무리 {1}: 색도 ({2:F3},{3:F3}) 텍셀 {4} ({5:F0}%)", m.name, pass + 1, mr, mg, cnt, 100.0 * cnt / total);
                    if (o.skinOnly && !skin)
                    {
                        log.Add(head + string.Format(inv, " — 피부 아님, 그대로 둠 (얼굴색과 색도 거리 {0:F3}){1}", skinDist, why));
                        continue;
                    }

                    // ---- 잘라서 SkinFlatCore 에
                    int mgn = 8; cx0 = Math.Max(0, cx0 - mgn); cy0 = Math.Max(0, cy0 - mgn); cx1 = Math.Min(W - 1, cx1 + mgn); cy1 = Math.Min(H - 1, cy1 + mgn);
                    int cw = cx1 - cx0 + 1, ch = cy1 - cy0 + 1, CN = cw * ch;
                    var cRgba = new byte[CN * 4]; var cWt = new float[CN]; var cSw = new float[CN]; var cCov = new bool[CN];
                    for (int y = 0; y < ch; y++)
                    {
                        Buffer.BlockCopy(rgba, ((y + cy0) * W + cx0) * 4, cRgba, y * cw * 4, cw * 4);
                        for (int x = 0; x < cw; x++) cCov[y * cw + x] = cov[(y + cy0) * W + x + cx0];
                    }
                    for (int k = 0; k < cnt; k++) { int i = inS[k]; int j = (i / W - cy0) * cw + (i % W - cx0); cWt[j] = 1f; cSw[j] = 1f; }
                    var fo = Clone(o.flat);
                    if (skin)
                    {
                        // 피부: 얼굴의 평탄색으로 칠하고, 색 차이로는 남기지 않습니다(붉은 칠은 피부로 덮음). 어두운 선·넓은 어두운 면·밝은 조각의 기준은 얼굴과 같습니다.
                        fo.useFill = true; fo.fillR = fillR; fo.fillG = fillG; fo.fillB = fillB;
                        fo.tC0 = 1e6f; fo.tC1 = 1e6f;
                    }
                    else if (o.dropBroadShade) { fo.tG0 = 1e6f; fo.tG1 = 1e6f; fo.tBright = 1e6f; }
                    fo.minSizeFrac = o.regionMinSizeFrac;
                    float kY = o.relativeContrast ? Math.Max(o.minContrastScale, Math.Min(1f, yMean / o.refY)) : 1f;
                    fo.tL0 *= kY; fo.tL1 *= kY; fo.minEdge *= kY;
                    var res = SkinFlatCore.Run(cRgba, cw, ch, cWt, cSw, cCov, m.hwTex, null, null, fo);
                    if (skin) head += string.Format(inv, " 피부(얼굴색과 색도 거리 {0:F3}, 밝기 {1:F2}배{2})", skinDist, yRatio, widened > 0 ? ", 붉은 쪽 " + widened + "텍셀 포함" : "");
                    if (res.skipped) { log.Add(head + " — 그대로 둠: " + res.note + string.Format(inv, " (잔무늬 {0:F2})", res.gateFine)); continue; }
                    log.Add(head + string.Format(inv, " — 평탄화: 칠한 색 ({0},{1},{2}), 중앙색 ({3},{4},{5}), 잔무늬 {6:F2}, 남긴 비율 {7:F1}%, 남긴 성분 {8}{9}",
                        res.flatR, res.flatG, res.flatB, res.medR, res.medG, res.medB, res.gateFine, res.keepPct, res.comps, skin ? "" : why));
                    if (skin) m.skinClusters++;
                    // 무리의 텍셀과, 그 가장자리에서 여백으로 번진 텍셀만 옮깁니다 (다른 메시·다른 무리의 텍셀은 건드리지 않음)
                    for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
                    {
                        int j = y * cw + x, i = (y + cy0) * W + x + cx0;
                        if (cWt[j] <= 0f && cCov[j]) continue;
                        bool same = res.outRgba[j * 4] == cRgba[j * 4] && res.outRgba[j * 4 + 1] == cRgba[j * 4 + 1] && res.outRgba[j * 4 + 2] == cRgba[j * 4 + 2];
                        if (cWt[j] <= 0f && !cCov[j] && same) continue;
                        outp[i * 4] = res.outRgba[j * 4]; outp[i * 4 + 1] = res.outRgba[j * 4 + 1]; outp[i * 4 + 2] = res.outRgba[j * 4 + 2];
                        if (cWt[j] > 0f)
                        {
                            if (!same) m.changed++;
                            if (mask != null)
                            {
                                bool keptPx = res.F != null && res.F[j] > 0.5f;
                                mask[i * 4] = keptPx ? (byte)0 : (byte)255; mask[i * 4 + 1] = keptPx ? (byte)255 : (byte)0; mask[i * 4 + 2] = skin ? (byte)160 : (byte)0;
                            }
                        }
                    }
                }
            }
            return outp;
        }

        static SkinFlatCore.Opt Clone(SkinFlatCore.Opt a)
        {
            var b = new SkinFlatCore.Opt();
            b.tL0 = a.tL0; b.tL1 = a.tL1; b.tC0 = a.tC0; b.tC1 = a.tC1; b.tG0 = a.tG0; b.tG1 = a.tG1; b.tBright = a.tBright; b.excludeGlobal = a.excludeGlobal;
            b.radiusFrac = a.radiusFrac; b.growFrac = a.growFrac; b.minSizeFrac = a.minSizeFrac; b.minEdge = a.minEdge; b.feather = a.feather; b.featherFrac = a.featherFrac; b.featFlat = a.featFlat;
            b.flatLo = a.flatLo; b.flatHi = a.flatHi; b.gate = a.gate; b.gateRadiusFrac = a.gateRadiusFrac; b.force = a.force;
            b.useFill = a.useFill; b.fillR = a.fillR; b.fillG = a.fillG; b.fillB = a.fillB;
            return b;
        }
    }
}
