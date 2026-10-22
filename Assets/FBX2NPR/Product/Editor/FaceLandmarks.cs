using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 얼굴 기준점: 코끝과 두 눈의 중심·크기 (월드 좌표).
    /// 서브컬처 명암선의 코 음영(46번)과 눈 피하기(47번)가 쓴다.
    /// 실험 코드(GeneralizationTest)와 미리보기 씬(ToonFacePreview)이 같은 규칙을 쓰도록 한곳에 둔다.
    ///
    /// 코끝: 얼굴 메시 정점 중 정중선 근처에서 가장 앞(+Z)으로 나온 점.
    /// 눈: 이름에 eye·lash 가 들어간 메시(눈썹 제외)의 정점을 좌우로 나눠 중심과 크기를 잡는다.
    ///     믹사모 Ch07 은 눈이 몸 메시 안에 있어 속눈썹 메시로, The Boss 는 L/R_Eye_Geo, 유니티짱은 EYE_DEF·eye_L/R_old.
    ///     눈 메시가 없으면(Jones 는 통짜 메시) 코끝 기준 비율로 추정하고, 추정했다고 남긴다.
    /// 캐릭터는 +Z 를 보고 +Y 가 위라고 가정한다 (테스트 리그 기준. 머리 축 자동 추출은 남은 일).
    /// </summary>
    public class FaceLandmarks
    {
        public struct Src
        {
            public string name;
            public Vector3[] world;   // 월드 좌표 정점
            public float[] weights;   // 얼굴 가중치 (얼굴이 아닌 메시는 0)
            public bool isFace;
            public Vector3[] normals; // 월드 원본 노멀 (코 음영 면적 계산용, 없으면 그 메시는 건너뜀)
            public int[] triangles;
        }

        public bool faceFound;
        public Bounds face;           // 얼굴 메시(가중치 0.5 이상) 바운드
        public bool noseFound;
        public Vector3 noseTip;
        public bool chinFound;
        public Vector3 chinTip;       // 턱 끝: 명암선이 모이는 점 (49번)
        public Vector4 noseRadius;    // x 좌우, y 위, z 아래, w 뒤쪽 깊이
        public bool eyesFound;
        public bool eyesEstimated;    // 눈 메시가 없어 비율로 추정했는가
        public Vector3 eyeA, eyeB;    // eyeA = +X 쪽, eyeB = -X 쪽
        public Vector4 eyeRadius;     // x 좌우, y 위아래, z 뒤쪽 깊이
        public float eyeHalfWidth;    // 눈 하나의 좌우 반폭 (눈 중심 ± 이 값 = 눈꼬리·눈머리). 50번 명암선 기준점
        public string eyeSource = "";

        public static FaceLandmarks Detect(List<Src> parts)
        {
            var r = new FaceLandmarks();
            bool init = false;
            foreach (var p in parts)
            {
                if (!p.isFace || p.weights == null) continue;
                for (int i = 0; i < p.world.Length; i++)
                {
                    if (p.weights[i] < 0.5f) continue;
                    if (!init) { r.face = new Bounds(p.world[i], Vector3.zero); init = true; } else r.face.Encapsulate(p.world[i]);
                }
            }
            if (!init) return r;
            r.faceFound = true;
            Vector3 fc = r.face.center, fs = r.face.size;

            // 코끝
            float best = float.NegativeInfinity;
            foreach (var p in parts)
            {
                if (!p.isFace || p.weights == null) continue;
                for (int i = 0; i < p.world.Length; i++)
                {
                    if (p.weights[i] < 0.5f) continue;
                    var rel = p.world[i] - fc;
                    if (Mathf.Abs(rel.x) > 0.12f * fs.x) continue;
                    if (rel.y > 0.15f * fs.y || rel.y < -0.40f * fs.y) continue;
                    if (rel.z > best) { best = rel.z; r.noseTip = p.world[i]; r.noseFound = true; }
                }
            }
            // 코 영역 반경은 눈을 찾은 뒤에 정한다 (52번, 아래).

            // 턱 끝은 눈을 찾은 뒤에 구한다 (눈~코끝 거리로 찾을 높이를 정하므로).

            // 눈: 이름으로 찾은 메시를 좌우로 나눈다
            var a = new List<Vector3>(); var b = new List<Vector3>(); var names = new List<string>();
            foreach (var p in parts)
            {
                var nm = p.name.ToLowerInvariant();
                if (!(nm.Contains("eye") || nm.Contains("lash"))) continue;
                if (nm.Contains("brow") || nm.Contains("blw")) continue;
                names.Add(p.name);
                foreach (var w in p.world) { if (w.x >= fc.x) a.Add(w); else b.Add(w); }
            }
            if (a.Count >= 8 && b.Count >= 8)
            {
                // 최소·최대 대신 5~95 백분위로 잡는다. 유니티짱 눈 메시에는 동떨어진 정점이 섞여 있어
                // 바운드로 잡으니 눈 영역이 얼굴 전체(세로 반경 9cm)로 커졌다.
                Vector3 ca, ea, cb, eb;
                Robust(a, out ca, out ea); Robust(b, out cb, out eb);
                r.eyeA = ca; r.eyeB = cb;
                float ex = Mathf.Max(ea.x, eb.x), ey = Mathf.Max(ea.y, eb.y);
                r.eyeHalfWidth = ex;
                r.eyeRadius = new Vector4(
                    Mathf.Clamp(ex * 1.6f, 0.07f * fs.x, 0.16f * fs.x),
                    Mathf.Clamp(ey * 2.2f, 0.05f * fs.y, 0.11f * fs.y),
                    0.25f * fs.z, 0f);
                r.eyesFound = true; r.eyeSource = "메시 " + string.Join(",", names.ToArray());
            }
            else if (r.noseFound)
            {
                float z = r.noseTip.z - 0.12f * fs.z, y = r.noseTip.y + 0.26f * fs.y;
                r.eyeA = new Vector3(fc.x + 0.17f * fs.x, y, z);
                r.eyeB = new Vector3(fc.x - 0.17f * fs.x, y, z);
                r.eyeRadius = new Vector4(0.12f * fs.x, 0.07f * fs.y, 0.25f * fs.z, 0f);
                r.eyeHalfWidth = 0.08f * fs.x;
                r.eyesFound = true; r.eyesEstimated = true; r.eyeSource = "추정(눈 메시 없음, 코끝 기준 비율)";
            }

            // 코 음영 영역 (52번). 코 그림자는 콧구멍 바로 위, 콧방울 윗면에 붙어야 한다.
            // 46번 값(얼굴 메시 바운드의 좌우 11%, 위 18%, 아래 5%)은 좌우가 좁아 콧방울이 타원 가장자리에 걸렸고,
            // 가장자리에서는 임계값을 낮춰 그림자를 지우므로 콧방울 쪽은 잘리고 콧대 옆면만 떠 있는 띠로 남았다
            // (사용자: "코에 뭐가 묻은 것 같다"). 좌우는 콧방울까지 넉넉히, 위로는 코 아래쪽까지만 잡는다.
            // 얼굴 메시 바운드는 캐릭터마다 목·뒤통수가 섞여 크기가 제각각이라(The Boss 는 높이 45cm),
            // 눈을 메시로 찾았으면 눈 사이 거리와 눈~코끝 높이로 잰다. 위 반경이 작으면 그림자 윗변이 타원 모양대로 둥글게 잘린다.
            if (r.eyesFound && !r.eyesEstimated && 0.5f * (r.eyeA.y + r.eyeB.y) - r.noseTip.y > 0.02f * fs.y)
            {
                float es = Mathf.Abs(r.eyeA.x - r.eyeB.x);
                float dEN = 0.5f * (r.eyeA.y + r.eyeB.y) - r.noseTip.y;
                r.noseRadius = new Vector4(0.55f * es, 1.05f * dEN, 0.45f * dEN, 0.18f * fs.z);
            }
            else r.noseRadius = new Vector4(0.22f * fs.x, 0.16f * fs.y, 0.07f * fs.y, 0.18f * fs.z);

            // 턱 끝: 정중선 근처, 코끝에서 얼굴 깊이 25% 이내로 앞쪽에 있는 정점 중 가장 낮은 점.
            // 목은 턱보다 한참 뒤에 있어서 깊이 조건으로 빠진다. 다만 The Boss 는 배가 앞으로 나와서 가슴을 턱으로 잡았다(코끝 24cm 아래).
            // 그래서 찾는 높이를 제한한다: 얼굴 삼등분 비율상 코끝~턱은 눈~코끝의 1.2~3배 (Ch07 2.2배, 유니티짱 1.6배).
            if (r.noseFound)
            {
                float dEN = (r.eyesFound ? 0.5f * (r.eyeA.y + r.eyeB.y) : r.noseTip.y + 0.15f * fs.y) - r.noseTip.y;
                if (dEN < 0.02f * fs.y) dEN = 0.15f * fs.y;
                float yHi = r.noseTip.y - 1.2f * dEN, yLo = r.noseTip.y - 3.0f * dEN;
                float lowest = float.PositiveInfinity;
                foreach (var p in parts)
                {
                    if (!p.isFace || p.weights == null) continue;
                    for (int i = 0; i < p.world.Length; i++)
                    {
                        if (p.weights[i] < 0.5f) continue;
                        var w = p.world[i];
                        if (Mathf.Abs(w.x - fc.x) > 0.10f * fs.x) continue;
                        if (w.z < r.noseTip.z - 0.25f * fs.z) continue;
                        if (w.y > yHi || w.y < yLo) continue;
                        if (w.y < lowest) { lowest = w.y; r.chinTip = w; r.chinFound = true; }
                    }
                }
            }
            return r;
        }

        // 50번 기준점: 눈꼬리 살짝 바깥, 턱 끝 조금 옆 턱선. 둘 다 얼굴 표면 위의 점. A = +X 쪽, B = -X 쪽.
        public bool anchorsFound;
        public Vector3 anchorCA, anchorCB, anchorJA, anchorJB;
        // 얼굴 앞면 윤곽의 높이별 가로 반폭 (얼굴 중심 x 기준). 명암선은 이 윤곽을 비율만큼 줄인 곡선이다.
        // 사용자가 그린 선은 네 캐릭터 모두 얼굴 윤곽과 나란히 조금 안쪽을 따라 내려갔다(V 자 얼굴은 사선, 네모 얼굴은 세로 후 턱에서 꺾임).
        public const int SilBins = 16;
        public float[] silA = new float[SilBins], silB = new float[SilBins];
        public float silV0, silV1;

        /// <summary>
        /// 명암선 기준점을 얼굴 표면 위에 잡는다.
        /// 처음에는 눈 메시 중심 ± 반폭을 그대로 썼는데, 유니티짱은 눈 메시가 표면보다 2cm 이상 안쪽이라
        /// 그 점의 방향으로 잰 임계값이 크게 어긋나 얼굴 그림자가 통째로 사라졌다. 표면 점이면 그 점의 노멀과 방향이 같다.
        /// marginFrac: 눈꼬리 바깥 여백(눈 반폭 대비), jawFrac: 턱 끝에서 옆으로(얼굴 폭 대비).
        /// </summary>
        public void ComputeAnchors(List<Src> parts, float marginFrac, float jawFrac)
        {
            anchorsFound = false;
            if (!faceFound || !eyesFound || !noseFound) return;
            float fx = face.size.x;
            Vector3 chin = ChinPoint();
            float jy = chin.y + 0.15f * (noseTip.y - chin.y);   // 턱 끝보다 조금 위, 턱 앞면
            bool a, b, ja, jb;
            anchorCA = FrontSurface(parts, new Vector2(eyeA.x + eyeHalfWidth * (1f + marginFrac), eyeA.y), out a);
            anchorCB = FrontSurface(parts, new Vector2(eyeB.x - eyeHalfWidth * (1f + marginFrac), eyeB.y), out b);
            anchorJA = FrontSurface(parts, new Vector2(chin.x + jawFrac * fx, jy), out ja);
            anchorJB = FrontSurface(parts, new Vector2(chin.x - jawFrac * fx, jy), out jb);
            anchorsFound = a && b && ja && jb;
        }

        /// <summary>
        /// 정면에서 본 (x, y) 에 가장 가까운 얼굴 앞면 점.
        /// 앞면만 본다(코끝에서 얼굴 깊이 40% 이내). 유니티짱은 눈이 커서 눈꼬리 바깥 자리가 얼굴 앞면을 벗어났고,
        /// 그 자리의 가장 앞쪽 정점이 뒤통수(z 음수)라 기준점이 머리 뒤에 잡혔다.
        /// 목표 자리에 앞면이 없으면 가장 가까운 앞면 점(대개 얼굴 옆 가장자리)을 쓴다.
        /// </summary>
        Vector3 FrontSurface(List<Src> parts, Vector2 xy, out bool ok)
        {
            float zMin = noseTip.z - 0.4f * face.size.z;
            // 1) 목표에 가장 가까운 앞면 정점
            float bestD = float.PositiveInfinity; Vector3 near = Vector3.zero;
            foreach (var p in parts)
            {
                if (!p.isFace || p.weights == null) continue;
                for (int i = 0; i < p.world.Length; i++)
                {
                    if (p.weights[i] < 0.5f) continue;
                    var w = p.world[i];
                    if (w.z < zMin) continue;
                    float dx = w.x - xy.x, dy = w.y - xy.y, d = dx * dx + dy * dy;
                    if (d < bestD) { bestD = d; near = w; }
                }
            }
            if (float.IsPositiveInfinity(bestD)) { ok = false; return new Vector3(xy.x, xy.y, noseTip.z); }
            // 2) 그 자리에서 보이는 표면: 주변 3mm 안의 가장 앞쪽 z (눈알 안쪽·입속 같은 가려진 정점을 피한다)
            float rr = 0.003f, zTop = near.z;
            foreach (var p in parts)
            {
                if (!p.isFace || p.weights == null) continue;
                for (int i = 0; i < p.world.Length; i++)
                {
                    if (p.weights[i] < 0.5f) continue;
                    var w = p.world[i];
                    float dx = w.x - near.x, dy = w.y - near.y;
                    if (dx * dx + dy * dy <= rr * rr && w.z > zTop) zTop = w.z;
                }
            }
            ok = true;
            return new Vector3(near.x, near.y, zTop);
        }

        /// <summary>
        /// 코 음영 면적 (52번): 임계값별로 정면에서 보이는 코 그림자 면적(m²)을 셰이더와 같은 식으로 계산한다.
        /// 얼굴 메시를 코 영역만 작은 깊이 버퍼로 그려 보이는 면만 센다. 처음에는 삼각형을 전부 셌더니
        /// 콧구멍 안쪽처럼 정면에서 가려진 면까지 들어가 Ch07 의 최소값이 -1.00 으로 나왔다.
        /// sMinVisible: 코 영역 안쪽(임계값이 줄지 않는 곳)에서 보이는 면의 가장 작은 수평 ndotl.
        /// </summary>
        public float[] NoseShadowArea(List<Src> parts, Vector3 lightDir, float[] thresholds, out float sMinVisible)
        {
            float sMin = float.PositiveInfinity;
            var area = new float[thresholds.Length];
            ForEachNosePixel(parts, lightDir, (u, v, s, mN, r, pa) =>
            {
                if (r < 0.55f) sMin = Mathf.Min(sMin, s);
                for (int i = 0; i < thresholds.Length; i++) if (s < thresholds[i] - (1f - mN) * 2f) area[i] += pa;
            });
            sMinVisible = sMin;
            return area;
        }

        /// <summary>
        /// 코 영역을 정면 깊이 버퍼로 그려, 보이는 픽셀마다 (u 좌우, v 위아래: 코끝 기준 m, s 원본 노멀 수평 ndotl,
        /// mN 영역 마스크, r 타원 반경비, 픽셀 면적 m²) 를 넘긴다. 셰이더의 코 음영과 같은 식.
        /// </summary>
        void ForEachNosePixel(List<Src> parts, Vector3 lightDir, System.Action<float, float, float, float, float, float> visit)
        {
            if (!noseFound) return;
            Vector3 up = Vector3.up, fwd = Vector3.forward, right = Vector3.Cross(up, fwd).normalized;
            Vector3 lh = lightDir - up * Vector3.Dot(lightDir, up);
            if (lh.sqrMagnitude < 1e-8f) return;
            lh.Normalize();
            var nr = noseRadius;
            float px = Mathf.Min(nr.x, nr.y) / 150f;   // 픽셀 한 칸 (Ch07 약 0.23mm)
            int W = Mathf.CeilToInt(2f * nr.x / px), H = Mathf.CeilToInt((nr.y + nr.z) / px);
            float u0 = -nr.x, v0 = -nr.z;
            var depth = new float[W * H]; var nrm = new Vector3[W * H];
            for (int k = 0; k < depth.Length; k++) depth[k] = float.NegativeInfinity;
            foreach (var p in parts)
            {
                if (!p.isFace || p.normals == null || p.triangles == null) continue;
                var tri = p.triangles;
                for (int t = 0; t + 2 < tri.Length; t += 3)
                {
                    int ia = tri[t], ib = tri[t + 1], ic = tri[t + 2];
                    Vector3 A = p.world[ia] - noseTip, B = p.world[ib] - noseTip, C = p.world[ic] - noseTip;
                    float au = Vector3.Dot(A, right), av = Vector3.Dot(A, up), az = Vector3.Dot(A, fwd);
                    float bu = Vector3.Dot(B, right), bv = Vector3.Dot(B, up), bz = Vector3.Dot(B, fwd);
                    float cu = Vector3.Dot(C, right), cv = Vector3.Dot(C, up), cz = Vector3.Dot(C, fwd);
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(au, Mathf.Min(bu, cu)) - u0) / px));
                    int x1 = Mathf.Min(W - 1, Mathf.FloorToInt((Mathf.Max(au, Mathf.Max(bu, cu)) - u0) / px));
                    int y0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(av, Mathf.Min(bv, cv)) - v0) / px));
                    int y1 = Mathf.Min(H - 1, Mathf.FloorToInt((Mathf.Max(av, Mathf.Max(bv, cv)) - v0) / px));
                    if (x0 > x1 || y0 > y1) continue;
                    float den = (bv - cv) * (au - cu) + (cu - bu) * (av - cv);
                    if (Mathf.Abs(den) < 1e-14f) continue;
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            float pu = u0 + (x + 0.5f) * px, pv = v0 + (y + 0.5f) * px;
                            float wa = ((bv - cv) * (pu - cu) + (cu - bu) * (pv - cv)) / den;
                            float wb = ((cv - av) * (pu - cu) + (au - cu) * (pv - cv)) / den;
                            float wc = 1f - wa - wb;
                            if (wa < 0f || wb < 0f || wc < 0f) continue;
                            float z = wa * az + wb * bz + wc * cz;
                            int k = y * W + x;
                            if (z <= depth[k]) continue;
                            depth[k] = z;
                            nrm[k] = p.normals[ia] * wa + p.normals[ib] * wb + p.normals[ic] * wc;
                        }
                }
            }
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int k = y * W + x;
                    if (float.IsNegativeInfinity(depth[k]) || depth[k] < -nr.w) continue;
                    float pu = u0 + (x + 0.5f) * px, pv = v0 + (y + 0.5f) * px;
                    float nx = pu / nr.x, ny = pv / (pv > 0f ? nr.y : nr.z);
                    float r = Mathf.Sqrt(nx * nx + ny * ny);
                    if (r >= 1f) continue;
                    float x01 = Mathf.Clamp01((r - 0.55f) / 0.45f);
                    float mN = 1f - x01 * x01 * (3f - 2f * x01);
                    var n = nrm[k].normalized;
                    var h = n - up * Vector3.Dot(n, up);
                    float s = Vector3.Dot(h, lh) / Mathf.Max(h.magnitude, 0.35f);
                    visit(pu, pv, s, mN, r, px * px);
                }
        }

        // 코 반달 (54번): 코가 평평한 캐릭터용. 셰이더 _NoseMoonShape / _NoseMoonParam 과 같은 뜻.
        public bool moonOn;
        public Vector4 moonShape;   // x 코 중심선에서 곧은 변까지, y 중심 높이(코끝 기준), z 폭, w 반높이 (m)
        public Vector4 moonParam;   // x 기준 빛의 옆 성분 |lh·right|, y 깊이 한계 (m), z 기준 빛에서의 초승달 두께 (0~1, 1 이면 반달)
        public const float MoonThickness = 0.5f;   // 55번 기본 초승달 두께
        public Vector4 moonExtra;   // 59번: x 초승달의 가장 두꺼운 곳 위치 (-0.9 아래 ~ 0.9 위)
        public string moonNote = "코 반달 안 씀";

        /// <summary>
        /// 코 반달의 자리·크기를 잰다 (54번). 유니티짱처럼 코가 평평해 임계값을 올린 캐릭터(noseStop > 0.05)만.
        /// 보정한 임계값에서 원본 노멀 그림자가 생기는 픽셀(사용자가 "위치는 아주 적절"하다고 한 반달)을 모아
        /// 코 중심선 쪽 가장자리(2 백분위)를 곧은 변, 바깥 끝(98 백분위)을 호의 끝, 위아래(2~98 백분위)를 높이로 쓴다.
        /// 원본 노멀 그대로는 큰 메시 면을 따라 경계가 꺾여 "사다리꼴이 돌아 있는" 모양이었다.
        /// </summary>
        /// <param name="shape">0 초승달, 1 세모 (56번), 2 둥근 사다리꼴 (57번: 원래 원본 노멀 그림자의 윤곽을 사각형으로)</param>
        /// <param name="attach">콧선 쪽 기준선을 코 중심선 바로 옆(눈~코끝 높이의 2.5%)으로 붙인다. 바깥 끝은 그대로라 폭이 넓어진다.</param>
        /// <param name="heightScale">밑(코끝 높이)은 그대로 두고 위로 늘인다 (세모를 가늘고 길게)</param>
        public void FitNoseMoon(List<Src> parts, Vector3 lightDir, float thickness = MoonThickness, int shape = 0, bool attach = false, float heightScale = 1f, float bulge = 0f, float widthScale = 1f,
                                float dropRadius = DropRadiusFrac, float dropHeight = DropHeightFrac)
        {
            moonOn = false;
            moonExtra = new Vector4(Mathf.Clamp(bulge, -0.9f, 0.9f), 0f, 0f, 0f);
            if (!noseFound || noseStop <= 0.05f) { moonNote = string.Format(CultureInfo.InvariantCulture, "코 반달 안 씀 (임계값 {0:F2}, 코에 형태가 있어 원본 노멀 그림자)", noseStop); return; }
            Vector3 up = Vector3.up, fwd = Vector3.forward, right = Vector3.Cross(up, fwd).normalized;
            Vector3 lh = lightDir - up * Vector3.Dot(lightDir, up); lh.Normalize();
            float side = Vector3.Dot(lh, right);
            float sgn = side >= 0f ? -1f : 1f;   // 그림자 쪽
            var us = new List<float>(); var vs = new List<float>(); var pts = new List<Vector2>();
            float t = noseStop;
            ForEachNosePixel(parts, lightDir, (u, v, s, mN, r, pa) =>
            {
                if (s < t - (1f - mN) * 2f) { us.Add(u * sgn); vs.Add(v); pts.Add(new Vector2(u * sgn, v)); }
            });
            if (us.Count < 20) { moonNote = "코 반달 안 씀 (그림자 픽셀 " + us.Count + "개)"; return; }
            FitQuad(pts);   // 리그에서 모양을 바꿔도 되도록 둥근 사다리꼴 꼭짓점은 항상 잰다
            {
                // 61번 물방울도 항상 채운다: 아래 끝은 원래 그림자의 아래 끝(2 백분위), 크기는 눈~코끝 높이 비율
                var vsd = new List<float>(vs); vsd.Sort();
                FitDrop(vsd[Mathf.Clamp(Mathf.RoundToInt(0.02f * (vsd.Count - 1)), 0, vsd.Count - 1)], dropRadius, dropHeight);
            }
            us.Sort(); vs.Sort();
            System.Func<List<float>, float, float> pct = (l, q) => l[Mathf.Clamp(Mathf.RoundToInt(q * (l.Count - 1)), 0, l.Count - 1)];
            float uIn = pct(us, 0.02f), uOut = pct(us, 0.98f), vLo = pct(vs, 0.02f), vHi = pct(vs, 0.98f);
            moonShape = new Vector4(uIn, 0.5f * (vLo + vHi), Mathf.Max(uOut - uIn, 1e-4f), Mathf.Max(0.5f * (vHi - vLo), 1e-4f));
            if (attach) { float outer = moonShape.x + moonShape.z; moonShape.x = 0.025f * EyeNoseDrop(); moonShape.z = Mathf.Max(outer - moonShape.x, 1e-4f); }
            if (!Mathf.Approximately(heightScale, 1f)) { float bottom = moonShape.y - moonShape.w; moonShape.w *= heightScale; moonShape.y = bottom + moonShape.w; }
            moonShape.z *= Mathf.Max(widthScale, 0.01f);   // 60번: 바깥 폭 배율 (콧선 쪽 끝은 그대로)
            moonParam = new Vector4(Mathf.Abs(side), 0.6f * EyeNoseDrop(), Mathf.Clamp01(thickness), Mathf.Clamp(shape, 0, 3));
            moonOn = true;
            string[] shapeNames = { "초승달", "세모", "둥근 사다리꼴", "물방울" };
            moonNote = string.Format(CultureInfo.InvariantCulture,
                "코 {7}: 콧선 쪽 기준선 {0:F4}m 옆, 중심 높이 {1:F4}m, 폭 {2:F4}m, 반높이 {3:F4}m, 두께 {6:F2} (그림자 픽셀 {4}개, 임계값 {5:F2})",
                moonShape.x, moonShape.y, moonShape.z, moonShape.w, us.Count, t, moonParam.z, shapeNames[Mathf.Clamp(shape, 0, 3)]);
            if (shape == 2) moonNote += string.Format(CultureInfo.InvariantCulture, " / 꼭짓점 ({0:F4},{1:F4}) ({2:F4},{3:F4}) ({4:F4},{5:F4}) ({6:F4},{7:F4})",
                quadA.x, quadA.y, quadA.z, quadA.w, quadB.x, quadB.y, quadB.z, quadB.w);
            if (shape == 3) moonNote += string.Format(CultureInfo.InvariantCulture, " / 물방울 아래 원 ({0:F4},{1:F4}) r {4:F4}, 위 끝 ({2:F4},{3:F4}) r {5:F4}, 콧대 선 {6:F4}",
                dropA.x, dropA.y, dropA.z, dropA.w, dropR.x, dropR.y, dropR.z);
        }

        // 61번 물방울: dropA = (아래 원 중심 u', v, 위 끝 원 중심 u', v), dropR = (아래 반지름, 위 끝 반지름, 콧대 선 u', 0). 코끝 기준 m.
        public Vector4 dropA, dropR;
        // 기본 크기 (눈~코끝 높이 대비). 사용자 레퍼런스 일러스트는 반지름 약 0.13, 높이 약 0.37 (폭이 높이의 0.7배로 통통).
        public const float DropRadiusFrac = 0.10f, DropHeightFrac = 0.40f;

        /// <summary>
        /// 물방울 자리·크기 (61번). 레퍼런스처럼 왼쪽(콧선 쪽) 변이 콧대 선에 붙고, 둥근 아래는 코끝 옆, 뾰족한 위는 콧대 쪽.
        /// 아래 원은 콧대 선에 닿게(중심 = 콧대 선 + 반지름), 위 끝은 아래 원 중심보다 조금 콧대 쪽(0.6 반지름)이라 축이 살짝 기운다.
        /// </summary>
        void FitDrop(float vBottom, float rFrac, float hFrac)
        {
            float d = EyeNoseDrop();
            float u0 = 0.025f * d, R = rFrac * d, H = hFrac * d, rb = 0.12f * R;
            dropA = new Vector4(u0 + R, vBottom + R, u0 + 0.6f * R, vBottom + H - rb);
            dropR = new Vector4(R, rb, u0, 0f);
        }

        // 둥근 사다리꼴 (57번): 그림자 쪽 가로(u', 코 중심선에서 +), 세로(v, 코끝 기준 위 +) 좌표의 꼭짓점 4개, 반시계 순서.
        public Vector4 quadA, quadB;
        public const float QuadRound = 0.5f;   // 기본 모서리 둥글기 (ToonFaceRig.RoundQuadParams 의 round)

        /// <summary>
        /// 원래 원본 노멀 그림자 픽셀의 볼록 껍질을 꼭짓점 4개로 줄인다 (가장 작은 삼각형을 이루는 꼭짓점부터 뺌, Visvalingam).
        /// 안쪽 사각형이라 면적이 줄어드니 무게중심 기준으로 늘려 원래 그림자와 면적을 맞춘다.
        /// </summary>
        void FitQuad(List<Vector2> pts)
        {
            pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            System.Func<Vector2, Vector2, Vector2, float> cr = (o, a, b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            var lower = new List<Vector2>(); var upper = new List<Vector2>();
            foreach (var p in pts) { while (lower.Count >= 2 && cr(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0f) lower.RemoveAt(lower.Count - 1); lower.Add(p); }
            for (int i = pts.Count - 1; i >= 0; i--) { var p = pts[i]; while (upper.Count >= 2 && cr(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0f) upper.RemoveAt(upper.Count - 1); upper.Add(p); }
            lower.RemoveAt(lower.Count - 1); upper.RemoveAt(upper.Count - 1);
            var hull = new List<Vector2>(lower); hull.AddRange(upper);   // 반시계
            System.Func<List<Vector2>, float> area = poly => { float s = 0f; for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; s += a.x * b.y - b.x * a.y; } return 0.5f * Mathf.Abs(s); };
            float hullArea = area(hull);
            var q = new List<Vector2>(hull);
            while (q.Count > 4)
            {
                int best = 0; float bestA = float.PositiveInfinity;
                for (int i = 0; i < q.Count; i++)
                {
                    float a = Mathf.Abs(cr(q[(i + q.Count - 1) % q.Count], q[i], q[(i + 1) % q.Count]));
                    if (a < bestA) { bestA = a; best = i; }
                }
                q.RemoveAt(best);
            }
            while (q.Count < 4) q.Add(q[q.Count - 1] + new Vector2(1e-5f, 0f));   // 점이 너무 적을 때 대비
            Vector2 c = (q[0] + q[1] + q[2] + q[3]) * 0.25f;
            float k = Mathf.Sqrt(hullArea / Mathf.Max(area(q), 1e-12f));
            for (int i = 0; i < 4; i++) q[i] = c + (q[i] - c) * k;
            quadA = new Vector4(q[0].x, q[0].y, q[1].x, q[1].y);
            quadB = new Vector4(q[2].x, q[2].y, q[3].x, q[3].y);
        }

        // 코 음영 임계값 자동 보정 결과 (52번)
        public float noseStop = 0f;
        public string noseStopNote = "코 임계값 보정 안 함";
        public const float NoseMinAreaFrac = 0.035f;   // 코 그림자 최소 면적 = 눈~코끝 높이 제곱의 3.5%

        /// <summary>
        /// 코 음영 임계값을 캐릭터마다 정한다 (52번).
        /// 기본은 0: 원본 노멀이 빛 반대쪽(수평)을 향한 면만 그림자. Ch07 처럼 콧대 옆면이 가파른 코는 이것만으로
        /// 콧구멍 바로 위 물방울이 된다(0.25 는 콧대 옆 긴 띠라 "묻은 것 같다"는 지적을 받았다).
        /// 유니티짱처럼 거의 평평한 코는 0 에서 그림자가 아예 없다(보이는 면의 최소값 0.04). 그래서 보이는 그림자 면적이
        /// 최소 면적보다 작으면, 그 면적이 될 때까지 임계값을 올린다. 유니티짱은 0.25 근처(콧선 옆 반달)가 된다.
        /// 면적 기준은 두 캐릭터에서 쟀다: Ch07 0 에서 7.0%, 유니티짱 0.25 에서 3.5% (눈~코끝 높이 제곱 대비).
        /// </summary>
        public void CalibrateNoseStop(List<Src> parts, Vector3 lightDir)
        {
            noseStop = 0f;
            if (!noseFound) { noseStopNote = "코 임계값 보정: 코끝 없음, 0"; return; }
            var ts = new float[61];
            for (int i = 0; i < ts.Length; i++) ts[i] = 0.01f * i;   // 0.00 ~ 0.60
            float sMin;
            var area = NoseShadowArea(parts, lightDir, ts, out sMin);
            float d = EyeNoseDrop(), minA = NoseMinAreaFrac * d * d;
            if (area[0] >= minA)
            {
                noseStop = 0f;
            }
            else
            {
                noseStop = ts[ts.Length - 1];
                for (int i = 1; i < ts.Length; i++)
                {
                    if (area[i] < minA) continue;
                    float f = (minA - area[i - 1]) / Mathf.Max(area[i] - area[i - 1], 1e-12f);
                    noseStop = Mathf.Lerp(ts[i - 1], ts[i], Mathf.Clamp01(f));
                    break;
                }
            }
            noseStopNote = string.Format(CultureInfo.InvariantCulture,
                "코 임계값 보정: {0:F2} (0 에서 면적 {1:F1}%, 최소 {2:F1}%, 보이는 면 최소 s {3:F2})",
                noseStop, 100f * area[0] / (d * d), 100f * NoseMinAreaFrac, sMin);
        }

        /// <summary>눈~코끝 높이 (코 크기의 기준 길이). 눈을 메시로 못 찾았으면 얼굴 높이의 15%.</summary>
        public float EyeNoseDrop()
        {
            if (noseFound && eyesFound && !eyesEstimated)
            {
                float d = 0.5f * (eyeA.y + eyeB.y) - noseTip.y;
                if (d > 0.02f * face.size.y) return d;
            }
            return 0.15f * face.size.y;
        }

        /// <summary>턱 끝 위치. 못 찾았으면 코끝 아래 ChinDrop 만큼, 조금 뒤쪽으로 추정한다.</summary>
        public Vector3 ChinPoint()
        {
            if (chinFound) return chinTip;
            return new Vector3(face.center.x, noseTip.y - ChinDrop(), noseTip.z - 0.12f * face.size.z);
        }

        /// <summary>코끝에서 턱 끝까지 높이 차 (명암선을 턱으로 모으는 기준 거리). 턱을 못 찾으면 얼굴 높이의 30%.</summary>
        public float ChinDrop()
        {
            if (noseFound && chinFound && noseTip.y - chinTip.y > 1e-4f) return noseTip.y - chinTip.y;
            return 0.3f * face.size.y;
        }

        /// <summary>축별 5~95 백분위 구간의 중심과 반폭.</summary>
        static void Robust(List<Vector3> pts, out Vector3 center, out Vector3 half)
        {
            center = Vector3.zero; half = Vector3.zero;
            for (int k = 0; k < 3; k++)
            {
                var vals = new float[pts.Count];
                for (int i = 0; i < pts.Count; i++) vals[i] = pts[i][k];
                System.Array.Sort(vals);
                float lo = vals[(int)(0.05f * (vals.Length - 1))], hi = vals[(int)(0.95f * (vals.Length - 1))];
                center[k] = 0.5f * (lo + hi); half[k] = 0.5f * (hi - lo);
            }
        }

        static string V(Vector3 v) { return string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3})", v.x, v.y, v.z); }
        static string V(Vector4 v) { return string.Format(CultureInfo.InvariantCulture, "({0:F3}, {1:F3}, {2:F3}, {3:F3})", v.x, v.y, v.z, v.w); }

        public string Describe()
        {
            if (!faceFound) return "얼굴 기준점: 얼굴 메시 없음";
            return "얼굴 메시 바운드 중심 " + V(face.center) + " 크기 " + V(face.size)
                + " / 코끝 " + (noseFound ? V(noseTip) : "못 찾음") + " 반경 " + V(noseRadius)
                + " / 턱 끝 " + (chinFound ? V(chinTip) + string.Format(CultureInfo.InvariantCulture, " (코끝에서 {0:F3}m 아래)", noseTip.y - chinTip.y) : "못 찾음")
                + " / 눈 " + (eyesFound ? V(eyeA) + " " + V(eyeB) + " 반경 " + V(eyeRadius) + string.Format(CultureInfo.InvariantCulture, " 반폭 {0:F3}", eyeHalfWidth) + " [" + eyeSource + "]" : "못 찾음");
        }
    }
}
