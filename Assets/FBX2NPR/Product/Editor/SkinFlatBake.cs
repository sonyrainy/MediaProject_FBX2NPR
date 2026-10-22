// SkinFlatBake.cs
// SkinFlatCore(순수 계산)를 유니티 메시·텍스처에 연결합니다.
//   1) 알베도를 CPU 로 읽어 옵니다 (원본 에셋과 임포트 설정은 건드리지 않습니다).
//   2) 얼굴 메시의 삼각형을 UV 공간에 그려, 텍셀마다 얼굴 가중치와 3D 위치를 얻습니다.
//      같은 텍스처에 손·옷·안구 섬이 같이 있어도 얼굴 피부 텍셀만 고칩니다.
//      (얼굴 가중치가 0~0.5 인 목 쪽 전이 구간은 가중치만큼만 섞습니다.)
//   3) 안구·이빨처럼 피부와 이어지지 않은 작은 덩어리는 손대지 않습니다 (메시 연결 성분으로 가립니다).
//      피부에 이어 붙은 안쪽 면(입 안, 눈구멍 테)은 이 규칙으로 못 가리고, 색이 어두워서 '남긴 성분'으로 남습니다.
//   한계: 서브메시마다 다른 텍스처를 쓰는 메시는 구분하지 않습니다(첫 재질 기준). 얼굴 앞은 +Z, 위는 +Y 로 가정합니다.
//   4) 결과를 새 텍스처(메모리)로 돌려줍니다. 파일 저장은 호출한 쪽이 합니다.
using System.Collections.Generic;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class SkinFlatBake
    {
        public class Src
        {
            public string name; public Mesh mesh; public Matrix4x4 l2w; public float[] weights;
            public bool labelOk = true;   // 라벨 경로를 적용해도 되는 메시(피부 규칙에 맞고 눈·이가 아닌 것). 안구 섬이 '피부'로 라벨돼 칠해지는 것을 막는다
        }

        /// <summary>얼굴 파싱 라벨 맵(FBX2NPRParseBake 결과). 있으면 얼굴 가중치 대신 라벨이 '어디를 칠하고 어디를 남길지'를 정합니다.</summary>
        public class Labels
        {
            public int W, H; public byte[] label; public bool[] filled;
            public bool trustFeatures;    // 파서가 눈·눈썹을 찾았는가. 못 찾았으면(손그림 The Boss) 피부 라벨 안에서도 예전 대비 규칙으로 눈썹·입술을 남긴다(폴백)
            public FeatureDrawCore.Opt draw;   // null 이 아니면 이목구비를 사진 조각으로 남기지 않고 라벨 모양대로 그린다 (대비 규칙은 전부 끔)
            public static bool IsFlat(byte L) { return L == FaceParser.Skin || L == FaceParser.Neck || L == FaceParser.LEar || L == FaceParser.REar || L == FaceParser.Nose; }
            // 피부 메시 위에서 '그대로 둘' 라벨은 이목구비와 머리카락(두피에 그린 머리선)뿐이다. 옷·모자·안경·귀걸이·목걸이 라벨이 피부 메시에 떨어지면
            // 파서의 혼동(Ch07 가슴 그늘을 옷으로)이거나 이웃으로 번진 것이므로 라벨을 쓰지 않고 몸 단계(색 무리)에 맡긴다.
            public static bool IsKeep(byte L) { return L == FaceParser.LBrow || L == FaceParser.RBrow || L == FaceParser.LEye || L == FaceParser.REye || L == FaceParser.Mouth || L == FaceParser.ULip || L == FaceParser.LLip || L == FaceParser.Hair; }
            // 그릴 때 사진 대신 그려 넣는 라벨 (눈 구멍·머리카락은 그리지 않고 그대로 둔다)
            public static bool IsDrawn(byte L) { return L == FaceParser.LBrow || L == FaceParser.RBrow || L == FaceParser.Mouth || L == FaceParser.ULip || L == FaceParser.LLip; }
        }

        public class Result
        {
            public bool[] labelDone;      // 라벨 경로가 맡은 텍셀(텍스처 크기). 몸 단계는 이 텍셀을 건드리지 않는다. 라벨이 없으면 null
            public int labelFlat, labelKeep; public string labelNote = "";
            public FeatureDrawCore.Result drawn;   // 이목구비를 그린 결과 (안 그렸으면 null)
            public SkinFlatCore.Result core;
            public Texture2D albedo;      // 구운 알베도 (건너뛰면 null)
            public Texture2D mask;        // 등급 마스크(빨강 평탄화, 초록 남김, 노랑 건너뜀, 하늘 전이, 파랑 대상 아님 — SkinFlatCore.Result.maskRgba 참고). 밉 없음·포인트 필터
            public Texture2D copy;        // 무가공 사본. 원본(압축·임포터 밉) 대신 이것과 비교해야 공정합니다
            public int W, H, cropW, cropH;
            public float rho, hwTex;      // 텍셀 밀도(텍셀/m), 기준 길이(눈 사이 거리의 20%, 텍셀)
            public int shellTris, otherTris, skippedTris, comps;   // 피부 껍질 삼각형 / 그 밖(안구·이빨) / UV 가 0~1 밖이라 건너뜀 / 메시 연결 성분 수
            public string note = "";
            public bool Skipped { get { return core == null || core.skipped; } }
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }
        /// <summary>텍스처 텍셀 (x,y) 에 대응하는 라벨 맵 색인 (텍셀 중심 기준 최근접 — 비정수 비율에서도 한 줄 어긋나지 않게)</summary>
        static int LabelIndex(int x, int y, int W, int H, int lw, int lh) { return ((2 * y + 1) * lh / (2 * H)) * lw + ((2 * x + 1) * lw / (2 * W)); }

        /// <param name="src">얼굴 알베도</param>
        /// <param name="parts">이 텍스처를 쓰는 메시 전부 (weights = 얼굴 가중치, 얼굴이 아닌 메시는 0). 얼굴이 아닌 메시도 넣어야
        /// 그 메시의 UV 섬을 '빈 여백'으로 보고 번짐으로 덮는 일이 없습니다.</param>
        /// <param name="m">얼굴 기준점 (눈·코끝)</param>
        /// <param name="killNostril">콧구멍 자리를 지울지. 기본은 끕니다 — 4체 시험에서 타원이 콧구멍을 반만 덮어 네모난 홈이 남았습니다.</param>
        public static Result Bake(Texture src, List<Src> parts, FaceLandmarks m, SkinFlatCore.Opt o, bool killNostril)
        {
            return Bake(src, parts, m, o, killNostril, null);
        }
        /// <param name="lab">얼굴 파싱 라벨 맵. null 이면 예전처럼 얼굴 가중치(머리 본)와 색 통계로만 정합니다</param>
        public static Result Bake(Texture src, List<Src> parts, FaceLandmarks m, SkinFlatCore.Opt o, bool killNostril, Labels lab)
        {
            var res = new Result();
            int W = src.width, H = src.height; res.W = W; res.H = H;
            Color32[] px = ReadSrc(src, W, H);
            res.copy = MakeTex(px, W, H, src, true);

            // ---- 기준 길이 (전부 월드 미터)
            float ipd = m.eyesFound ? (m.eyeA - m.eyeB).magnitude : 0.34f * m.face.size.x;
            float dEN = m.EyeNoseDrop(), dNC = m.ChinDrop();
            float eyeY = m.eyesFound ? 0.5f * (m.eyeA.y + m.eyeB.y) : m.noseTip.y + dEN;
            bool nostril = killNostril && m.noseFound && m.eyesFound && !m.eyesEstimated;

            // ---- 메시 연결 성분: 피부 껍질(큰 덩어리)만 대상. 안구·이빨 같은 작은 덩어리는 원본 유지.
            var shellOf = new List<bool[]>();     // 파트별, 삼각형별
            {
                var areas = new List<List<double>>(); var compOf = new List<int[]>(); double maxArea = 0;
                foreach (var p in parts)
                {
                    var v = p.mesh.vertices; var tri = p.mesh.triangles; int nv = v.Length;
                    var parent = new int[nv]; for (int i = 0; i < nv; i++) parent[i] = i;
                    var weld = new Dictionary<Vector3Int, int>();
                    for (int i = 0; i < nv; i++)
                    {
                        var w = p.l2w.MultiplyPoint3x4(v[i]);
                        var key = new Vector3Int(Mathf.RoundToInt(w.x * 20000f), Mathf.RoundToInt(w.y * 20000f), Mathf.RoundToInt(w.z * 20000f));   // 0.05mm 격자
                        int j; if (weld.TryGetValue(key, out j)) Union(parent, i, j); else weld[key] = i;
                    }
                    for (int t = 0; t < tri.Length; t += 3) { Union(parent, tri[t], tri[t + 1]); Union(parent, tri[t], tri[t + 2]); }
                    var idOf = new Dictionary<int, int>(); var a = new List<double>(); var co = new int[tri.Length / 3];
                    for (int t = 0; t < tri.Length; t += 3)
                    {
                        int r = Find(parent, tri[t]); int id; if (!idOf.TryGetValue(r, out id)) { id = a.Count; idOf[r] = id; a.Add(0); }
                        var p0 = p.l2w.MultiplyPoint3x4(v[tri[t]]); var p1 = p.l2w.MultiplyPoint3x4(v[tri[t + 1]]); var p2 = p.l2w.MultiplyPoint3x4(v[tri[t + 2]]);
                        // 넓이에 얼굴 가중치를 곱해 셉니다. 그냥 넓이로 세면 한 메시에 몸까지 붙은 캐릭터에서 전신이 기준이 되어,
                        // 머리가 따로 떨어진 메시였다면 얼굴 전체가 '작은 덩어리'로 빠집니다. 가중치 0 인 옷·몸 메시는 0 이 됩니다.
                        float wm = (p.weights[tri[t]] + p.weights[tri[t + 1]] + p.weights[tri[t + 2]]) / 3f;
                        a[id] += 0.5 * Vector3.Cross(p1 - p0, p2 - p0).magnitude * wm; co[t / 3] = id;
                    }
                    foreach (var x in a) if (x > maxArea) maxArea = x;
                    areas.Add(a); compOf.Add(co); res.comps += a.Count;
                }
                for (int k = 0; k < parts.Count; k++)
                {
                    var co = compOf[k]; var sh = new bool[co.Length];
                    // 얼굴 가중 넓이가 가장 큰 덩어리의 10% 이상이면 피부로 봅니다 (여러 메시로 갈라진 얼굴 대비). 안구는 3% 안팎입니다.
                    for (int t = 0; t < co.Length; t++) sh[t] = maxArea > 0 && areas[k][co[t]] >= 0.10 * maxArea;
                    shellOf.Add(sh);
                }
            }

            // ---- UV 래스터: 텍셀마다 덮임 / 얼굴 가중치 / 표본 가중치 / 콧구멍 지움 비중
            int N = W * H;
            var cov = new bool[N]; var wt = new float[N]; var sw = new float[N]; var kill = nostril ? new float[N] : null;
            var apt = lab != null ? new float[N] : null; var okMesh = lab != null ? new bool[N] : null;   // 라벨 경로용: 텍셀의 실제 넓이(표본 가중치), 라벨을 적용해도 되는 메시인가
            var protSmall = lab != null ? new bool[N] : null;   // 라벨 경로용: 얼굴에 붙은 작은 성분(안구·이·입 안). 얼굴 단계도 몸 단계도 건드리지 않는다
            double sumTex = 0, sumWorld = 0;
            int minX = W, minY = H, maxX = -1, maxY = -1;
            for (int k = 0; k < parts.Count; k++)
            {
                var p = parts[k]; var v = p.mesh.vertices; var uv = p.mesh.uv; var tri = p.mesh.triangles; var sh = shellOf[k];
                if (uv == null || uv.Length != v.Length) { res.note += p.name + ": UV 없음. "; continue; }
                for (int t = 0; t < tri.Length; t += 3)
                {
                    int i0 = tri[t], i1 = tri[t + 1], i2 = tri[t + 2];
                    Vector2 a = uv[i0], b = uv[i1], c = uv[i2];
                    // 타일 경계 밖 UV 는 삼각형 단위로 한 칸씩 당깁니다. 정점마다 감으면 경계를 넘는 삼각형이 텍스처 전체로 늘어납니다.
                    var off = new Vector2(Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), Mathf.Floor(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                    a -= off; b -= off; c -= off;
                    if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) > 1.0001f || Mathf.Max(a.y, Mathf.Max(b.y, c.y)) > 1.0001f) { res.skippedTris++; continue; }
                    float ax = a.x * W, ay = a.y * H, bx = b.x * W, by = b.y * H, cx = c.x * W, cy = c.y * H;
                    float det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                    float texArea = 0.5f * Mathf.Abs(det); if (texArea < 1e-6f) continue;
                    var P0 = p.l2w.MultiplyPoint3x4(v[i0]); var P1 = p.l2w.MultiplyPoint3x4(v[i1]); var P2 = p.l2w.MultiplyPoint3x4(v[i2]);
                    float worldArea = 0.5f * Vector3.Cross(P1 - P0, P2 - P0).magnitude;
                    bool shell = sh[t / 3];
                    float w0 = shell ? p.weights[i0] : 0f, w1 = shell ? p.weights[i1] : 0f, w2 = shell ? p.weights[i2] : 0f;
                    bool anyW = p.weights[i0] > 0f || p.weights[i1] > 0f || p.weights[i2] > 0f;
                    if (shell && anyW) res.shellTris++; else if (anyW) res.otherTris++;   // 얼굴 가중치가 있는 삼각형만 셉니다 (옷·몸은 빼고)
                    if (shell && w0 >= 0.5f && w1 >= 0.5f && w2 >= 0.5f) { sumTex += texArea; sumWorld += worldArea; }
                    // mm² 단위. 표본을 텍셀 수가 아니라 실제 넓이로 셉니다 (UV 가 넓게 펴진 섬의 과대표집 방지).
                    // UV 가 거의 접힌 가는 삼각형은 한 텍셀이 비정상적으로 큰 가중치를 얻으므로 표본에서 뺍니다.
                    float areaPerTexel = texArea >= 0.5f ? worldArea / texArea * 1e6f : 0f;

                    int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx)))), x1 = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(ax, Mathf.Max(bx, cx))));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy)))), y1 = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(ay, Mathf.Max(by, cy))));
                    float inv = 1f / det;
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                    {
                        float qx = x + 0.5f, qy = y + 0.5f;
                        float l1 = ((qx - ax) * (cy - ay) - (cx - ax) * (qy - ay)) * inv;
                        float l2 = ((bx - ax) * (qy - ay) - (qx - ax) * (by - ay)) * inv;
                        float l0 = 1f - l1 - l2;
                        if (l0 < -1e-4f || l1 < -1e-4f || l2 < -1e-4f) continue;
                        int i = y * W + x;
                        float w = l0 * w0 + l1 * w1 + l2 * w2;
                        if (cov[i] && w <= wt[i]) continue;               // 겹치면 가중치가 큰 쪽
                        cov[i] = true; wt[i] = w;
                        // 라벨은 피부 껍질(큰 연결 성분)에만. 안구·이 같은 작은 성분은 라벨이 '피부'여도 건드리지 않는다 — 눈꺼풀 뒤 2mm 안의 흰자가
                        // 깊이 검사를 통과해 피부 라벨을 받고 평탄색으로 칠해졌다(검토 실측: 안구 섬의 23.7%).
                        if (apt != null)
                        {
                            apt[i] = areaPerTexel; okMesh[i] = p.labelOk && shell;
                            // 몸 단계는 '끝난 곳'이 아닌 텍셀을 색 무리로 다시 칠한다. 예전에는 얼굴 가중치 0.98 이상이 끝난 곳이라 안구가 보호됐는데,
                            // 라벨 경로로 끝난 곳을 바꾸자 안구 흰자가 몸 단계의 피부 무리(붉은 쪽 확장)에 들어가 피부색으로 칠해졌다(실측, 2026-10-09).
                            protSmall[i] = p.labelOk && !shell && (l0 * p.weights[i0] + l1 * p.weights[i1] + l2 * p.weights[i2]) >= 0.5f;
                        }
                        if (w <= 0f) { sw[i] = 0f; if (kill != null) kill[i] = 0f; continue; }
                        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                        Vector3 P = l0 * P0 + l1 * P1 + l2 * P2;
                        // 피부 표본 창: 얼굴 앞면 가운데 (눈 위 ~ 턱 위, 좌우 눈 바깥까지)
                        bool inWin = w >= 0.9f && Mathf.Abs(P.x - m.face.center.x) <= 0.9f * ipd
                                     && P.y >= m.noseTip.y - 0.9f * dNC && P.y <= eyeY + 0.9f * dEN && P.z >= m.noseTip.z - ipd;
                        sw[i] = inWin ? areaPerTexel : 0f;
                        if (kill != null)
                        {
                            // 콧구멍: 코끝 바로 아래의 작은 타원. 셰이더의 코 영역과 같은 감쇠(0.55~1.0).
                            float dx = (P.x - m.noseTip.x) / (0.30f * ipd);
                            float dyc = P.y - (m.noseTip.y - 0.05f * dEN);
                            float dy = dyc / (dyc >= 0f ? 0.25f * dEN : 0.35f * dEN);
                            bool deep = P.z >= m.noseTip.z - 0.5f * dEN;
                            kill[i] = deep ? 1f - Smooth(0.55f, 1f, Mathf.Sqrt(dx * dx + dy * dy)) : 0f;
                        }
                    }
                }
            }
            if (sumWorld <= 1e-8 || maxX < 0) { res.note += "얼굴 텍셀 없음(밀도 계산 불가)"; res.core = new SkinFlatCore.Result { skipped = true, note = "얼굴 텍셀 없음" }; res.mask = MakeMask(cov, wt, W, H, src); return res; }
            res.rho = (float)System.Math.Sqrt(sumTex / sumWorld);
            // 기준 길이: 눈 사이 거리의 20% (사람 눈 반폭쯤). 눈 반폭을 그대로 쓰면 안 됩니다 —
            // 눈을 크게 그린 캐릭터(유니티짱 반폭 3.2cm)에서 반경·최소 넓이가 두 배가 되어 얇은 선이 전부 지워졌습니다.
            res.hwTex = 0.2f * ipd * res.rho;

            // ---- 라벨이 있으면 '어디'는 라벨이 정한다: 피부·목·귀·코 = 칠함(코 안에서만 콧구멍을 대비로 남김), 눈썹·눈·입술·입·머리카락·모자·옷 = 그대로 두되
            //      경계는 흐림, 파서가 못 본 곳(머리 밖)과 배경으로 본 곳 = 손대지 않음(몸 단계로). 얼굴 가중치의 전이 구간(0~0.5)은 쓰지 않는다 —
            //      그 구간이 '양쪽 평탄색 사이의 어두운 띠'와 '귀 절단'(76번)을 만들었다.
            float[] keep = null; bool[] allow = null; byte[] labelFull = null;
            bool drawing = lab != null && lab.draw != null;
            if (lab != null && lab.label != null && lab.label.Length == lab.W * lab.H)
            {
                keep = new float[N]; allow = new bool[N]; res.labelDone = new bool[N]; labelFull = new byte[N];
                for (int i = 0; i < N; i++) labelFull[i] = 255;
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                {
                    int i = y * W + x; if (!cov[i]) continue;
                    if (!okMesh[i]) { if (wt[i] > 0f) { wt[i] = 0f; sw[i] = 0f; } if (protSmall[i]) res.labelDone[i] = true; continue; }   // 안구·이 등: 라벨도 가중치도 안 쓰고, 몸 단계도 못 건드리게
                    int li = LabelIndex(x, y, W, H, lab.W, lab.H); byte L = lab.label[li]; labelFull[i] = L;
                    if (Labels.IsFlat(L))
                    {
                        wt[i] = 1f; sw[i] = (L == FaceParser.Skin && !lab.filled[li]) ? apt[i] : 0f;
                        // 그릴 때는 사진 조각을 하나도 남기지 않는다(대비 규칙 끔). 안 그릴 때는 코(콧구멍)에서만, 폴백이면 전부
                        allow[i] = !drawing && (L == FaceParser.Nose || !lab.trustFeatures); res.labelDone[i] = true; res.labelFlat++;
                    }
                    else if (Labels.IsKeep(L)) { wt[i] = 1f; sw[i] = 0f; keep[i] = drawing && Labels.IsDrawn(L) ? 0f : 1f; res.labelDone[i] = true; res.labelKeep++; }
                    else { wt[i] = 0f; sw[i] = 0f; }
                    if (kill != null) kill[i] = 0f;
                }
                // 눈 둘레는 대비 규칙을 켜 둔다. 파서의 '눈' 클래스는 눈 구멍뿐이라 눈꺼풀에 그린 속눈썹·아이라인이 '피부'로 떨어져 지워졌다
                // (사용자: "실사 기반 여자 속눈썹이 다 없어졌잖아"). 반경은 기준 길이의 1.2배 — Ch07 의 아이라인 날개가 눈꼬리에서 그만큼 뻗는다.
                // 그릴 때는 해당 없음 — 아이라인을 선으로 긋는다.
                if (lab.trustFeatures && !drawing)
                {
                    int rEye = Mathf.Max(2, Mathf.RoundToInt(1.2f * res.hwTex));
                    var eye = new bool[N]; var tmpD = new bool[N];
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                    {
                        int li = LabelIndex(x, y, W, H, lab.W, lab.H); byte L = lab.label[li];
                        eye[y * W + x] = L == FaceParser.LEye || L == FaceParser.REye;
                    }
                    for (int y = 0; y < H; y++) { int row = y * W; for (int x = 0; x < W; x++) { bool any = false; int x0 = Mathf.Max(0, x - rEye), x1 = Mathf.Min(W - 1, x + rEye); for (int xx = x0; xx <= x1; xx++) if (eye[row + xx]) { any = true; break; } tmpD[row + x] = any; } }
                    int nearEye = 0;
                    for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) { bool any = false; int y0 = Mathf.Max(0, y - rEye), y1 = Mathf.Min(H - 1, y + rEye); for (int yy = y0; yy <= y1; yy++) if (tmpD[yy * W + x]) { any = true; break; } if (any && res.labelDone[y * W + x] && !allow[y * W + x]) { allow[y * W + x] = true; nearEye++; } }
                    res.labelNote = "눈 둘레 " + rEye + "텍셀(" + nearEye + "개)은 대비 규칙 허용. ";
                }
                res.labelNote = "라벨: 칠함 " + res.labelFlat + ", 보존 " + res.labelKeep + " 텍셀, " + (lab.trustFeatures ? "눈·눈썹 라벨 신뢰(피부 안 대비 규칙은 코·눈 둘레에서만)" : "눈·눈썹을 못 찾아 피부 안 대비 규칙 폴백") + ". " + res.labelNote;
                minX = W; minY = H; maxX = -1; maxY = -1;
                for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) { if (wt[y * W + x] <= 0f) continue; if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                if (maxX < 0) { res.note += "라벨이 맡은 텍셀 없음"; res.core = new SkinFlatCore.Result { skipped = true, note = "라벨이 맡은 텍셀 없음" }; res.mask = MakeMask(cov, wt, W, H, src); return res; }
            }

            // ---- 얼굴 텍셀이 있는 사각형만 잘라서 계산 (2048² 전체를 돌 필요가 없습니다)
            int mg = 8; minX = Mathf.Max(0, minX - mg); minY = Mathf.Max(0, minY - mg); maxX = Mathf.Min(W - 1, maxX + mg); maxY = Mathf.Min(H - 1, maxY + mg);
            int cw = maxX - minX + 1, ch = maxY - minY + 1, CN = cw * ch; res.cropW = cw; res.cropH = ch;
            var cRgba = new byte[CN * 4]; var cWt = new float[CN]; var cSw = new float[CN]; var cCov = new bool[CN]; var cKill = kill != null ? new float[CN] : null;
            var cKeep = keep != null ? new float[CN] : null; var cAllow = allow != null ? new bool[CN] : null;
            for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
            {
                int i = (y + minY) * W + (x + minX), j = y * cw + x;
                cRgba[j * 4] = px[i].r; cRgba[j * 4 + 1] = px[i].g; cRgba[j * 4 + 2] = px[i].b; cRgba[j * 4 + 3] = px[i].a;
                cWt[j] = wt[i]; cSw[j] = sw[i]; cCov[j] = cov[i]; if (cKill != null) cKill[j] = kill[i];
                if (cKeep != null) { cKeep[j] = keep[i]; cAllow[j] = allow[i]; }
            }
            o.allowKeep = cAllow;
            res.core = SkinFlatCore.Run(cRgba, cw, ch, cWt, cSw, cCov, res.hwTex, cKeep, cKill, o);

            // ---- 텍스처로 되돌리기
            // 등급 마스크 (SkinFlatCore.Result.maskRgba 와 같은 색 규칙). 잘라낸 사각형 밖은 전부 대상 아님(파랑)입니다.
            var mk = new Color32[N];
            for (int i = 0; i < N; i++) mk[i] = new Color32(0, 0, 255, 255);
            if (res.core.maskRgba != null)
                for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
                {
                    int i = (y + minY) * W + (x + minX), j = (y * cw + x) * 4;
                    mk[i] = new Color32(res.core.maskRgba[j], res.core.maskRgba[j + 1], res.core.maskRgba[j + 2], 255);
                }
            res.mask = MakeMaskTex(mk, W, H, src);
            if (!res.core.skipped)
            {
                var outPx = (Color32[])px.Clone();
                for (int y = 0; y < ch; y++) for (int x = 0; x < cw; x++)
                {
                    int i = (y + minY) * W + (x + minX), j = (y * cw + x) * 4;
                    outPx[i] = new Color32(res.core.outRgba[j], res.core.outRgba[j + 1], res.core.outRgba[j + 2], res.core.outRgba[j + 3]);
                }
                // ---- 이목구비 그리기 (라벨 경로 + draw 옵션). 평탄화본 위에 라벨 모양대로 눈썹·눈매·콧구멍·입술을 그린다.
                if (drawing && labelFull != null)
                {
                    var rgbaFull = new byte[N * 4]; var origFull = new byte[N * 4]; var allowed = new bool[N];
                    for (int i = 0; i < N; i++)
                    {
                        rgbaFull[i * 4] = outPx[i].r; rgbaFull[i * 4 + 1] = outPx[i].g; rgbaFull[i * 4 + 2] = outPx[i].b; rgbaFull[i * 4 + 3] = outPx[i].a;
                        origFull[i * 4] = px[i].r; origFull[i * 4 + 1] = px[i].g; origFull[i * 4 + 2] = px[i].b; origFull[i * 4 + 3] = px[i].a;
                        allowed[i] = cov[i] && okMesh[i] && labelFull[i] != 255;
                    }
                    if (!string.IsNullOrEmpty(FBX2NPRConverter.DebugDir)) DumpDrawInputs(FBX2NPRConverter.DebugDir, src.name, rgbaFull, origFull, labelFull, allowed, W, H, res.core.flatR, res.core.flatG, res.core.flatB, res.hwTex);
                    res.drawn = FeatureDrawCore.Run(rgbaFull, origFull, W, H, labelFull, allowed, res.core.flatR, res.core.flatG, res.core.flatB, res.hwTex, lab.draw);
                    // 그린 텍셀은 라벨이 없어도(다문 입의 틈 등) 몸 평탄화가 다시 칠하지 않게 "끝난 곳"으로 (기록 95번)
                    if (res.drawn != null && res.drawn.painted != null && res.labelDone != null) for (int i = 0; i < N; i++) if (res.drawn.painted[i]) res.labelDone[i] = true;
                    for (int i = 0; i < N; i++) outPx[i] = new Color32(rgbaFull[i * 4], rgbaFull[i * 4 + 1], rgbaFull[i * 4 + 2], rgbaFull[i * 4 + 3]);
                }
                res.albedo = MakeTex(outPx, W, H, src, true);
            }
            return res;
        }

        /// <summary>그리기 입력을 오프라인 조정용으로 남긴다 (DebugDir 이 있을 때만). 라벨 PNG: R = 라벨 번호, G = 그려도 되는 텍셀 255.</summary>
        static void DumpDrawInputs(string dir, string name, byte[] rgba, byte[] orig, byte[] label, bool[] allowed, int W, int H, byte fr, byte fg, byte fb, float hwTex)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            System.Action<string, byte[]> save = (suffix, bytes) =>
            {
                var t = new Texture2D(W, H, TextureFormat.RGBA32, false, false); t.SetPixelData(bytes, 0); t.Apply(false, false);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name + suffix), t.EncodeToPNG()); Object.DestroyImmediate(t);
            };
            save("_draw_in.png", rgba); save("_draw_orig.png", orig);
            var lb = new byte[W * H * 4];
            for (int i = 0; i < W * H; i++) { lb[i * 4] = label[i]; lb[i * 4 + 1] = allowed[i] ? (byte)255 : (byte)0; lb[i * 4 + 3] = 255; }
            save("_draw_label.png", lb);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name + "_draw_info.txt"),
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "fill {0} {1} {2}\nhwTex {3}\nsize {4} {5}\n", fr, fg, fb, hwTex, W, H));
        }

        static int Find(int[] p, int i) { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }
        static void Union(int[] p, int a, int b) { a = Find(p, a); b = Find(p, b); if (a != b) p[a] = b; }

        /// <summary>임포트된 텍스처(읽기 불가·압축)를 GPU 를 거쳐 sRGB 8비트로 읽습니다. 행 0 = 아래 (GetPixels32 와 같은 방향).</summary>
        static Color32[] ReadSrc(Texture src, int W, int H)
        {
            var rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, W, H), 0, 0); t.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = t.GetPixels32(); Object.DestroyImmediate(t); return px;
        }

        static Texture2D MakeTex(Color32[] px, int W, int H, Texture like, bool mip)
        {
            var t = new Texture2D(W, H, TextureFormat.RGBA32, mip, false);     // sRGB
            t.wrapMode = like.wrapMode; t.filterMode = like.filterMode; t.anisoLevel = like.anisoLevel;
            t.SetPixels32(px); t.Apply(mip, false); return t;
        }
        // 마스크는 밉·보간을 끕니다. 보간되면 경계에서 등급이 섞여 가짜 화소가 생깁니다. 감싸기 방식은 알베도와 같게 둡니다(타일 밖 UV 대비).
        static Texture2D MakeMaskTex(Color32[] mk, int W, int H, Texture like)
        {
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false, true);
            t.wrapMode = like.wrapMode; t.filterMode = FilterMode.Point;
            t.SetPixels32(mk); t.Apply(false, false); return t;
        }
        // 굽지 못한 경우의 마스크: 대상은 노랑(손대지 않음), 전이 구간은 하늘, 나머지는 파랑.
        static Texture2D MakeMask(bool[] cov, float[] wt, int W, int H, Texture like)
        {
            var mk = new Color32[W * H];
            for (int i = 0; i < mk.Length; i++)
                mk[i] = !cov[i] || wt[i] <= 0f ? new Color32(0, 0, 255, 255) : (wt[i] >= 0.5f ? new Color32(255, 255, 0, 255) : new Color32(0, 128, 255, 255));
            return MakeMaskTex(mk, W, H, like);
        }
    }
}
