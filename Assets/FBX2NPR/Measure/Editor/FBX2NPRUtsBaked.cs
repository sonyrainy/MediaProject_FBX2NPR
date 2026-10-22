// FBX2NPRUtsBaked.cs — 공식 UTS 의 'Baked Normal(외곽선용 노멀)' 로 입 깨짐이 고쳐지는지 렌더로 확인한다. (FBX2NPRUtsCompare 의 partial)
//   (A) UTS · FBX2NPR 노멀 (직전 (3) 재현)
//   (B) UTS · FBX2NPR 노멀 + _Is_BakedNormal=1, _BakedNormal = 원본 노멀을 구운 텍스처 (메시마다 따로, 진짜 탄젠트로 다시 계산한 임시 메시)
//   (C) 기준: 정리된 노멀 메시(외곽선 패스 끔) + 같은 자리의 원본 노멀 메시(외곽선 패스만 켬)
//   (D) UTS · 원본 노멀 (직전 (2) 재현)      (E) FBX2NPR 툰 — 직전 이미지 재사용
// 셰이더 경로 (com.unity.toonshader 0.15.1, Runtime/Shaders/URP/UniversalToonOutline.hlsl):
//   35-38: N=UnityObjectToWorldNormal(v.normal), T=normalize(ObjectToWorld·v.tangent.xyz), B=normalize(cross(N,T)·v.tangent.w), 행 (T,B,N) — 셋 다 월드 공간
//   40: tex2Dlod(_BakedNormal, UV0(TRANSFORM_TEX), mip 0) * 2 - 1   (UnpackNormal 아님)
//   41: dir = normalize(c.x·T + c.y·B + c.z·N)  — 월드 방향
//   57-60 (_OUTLINE_NML): lerp(v.vertex + v.normal·w, v.vertex + dir·w, _Is_BakedNormal) — 오브젝트 공간 위치에 더한다
//   → 오브젝트 회전이 단위가 아니면 교과서식 탄젠트 공간 굽기는 회전만큼 어긋난다. 그래서 굽는 값 c 를 '렌더 공간 원본 노멀을 월드 TBN 으로 표현한 값'으로 푼다.
// 프리팹·재질·FBX·.meta 는 바꾸지 않는다. 임시 재질·메시·텍스처는 끝나면 지운다. 결과 NormalTestOut/uts_baked/.
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRUtsCompare.RunBaked   (측정만 다시: ...AnalyzeBaked)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace FBX2NPR.EditorTools
{
    public static partial class FBX2NPRUtsCompare
    {
        static string BakedDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "uts_baked"); } }
        static string CompareDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "uts_compare"); } }
        const int BakeRes = 2048;
        const int DilateTexels = 4;
        const float RasterConflictDeg = 2f, StampConflictDeg = 1f;
        static readonly string[] SkinHypNames = { "월드(보정 없음)", "루트 본 회전", "렌더러 트랜스폼 회전", "Renderer.localToWorldMatrix 회전" };

        class BakeTarget
        {
            public string path; public bool skinned;
            public Renderer rA, rB, rC1, rC2;
            public Mesh toonMesh, meshB, meshOrig;
            public Material uts, matB, matC1, matC2;
            public Vector2[] uv; public int[] tris;
            public Vector3[] Tw, Bw, Nw, NoW;     // 렌더 때 셰이더가 만드는 월드 T·B·N 과 원본 노멀(월드)
            public Quaternion[] hypR = new Quaternion[4];   // 정적: [0]=단위, [1]=트랜스폼 회전 / 스킨: SkinHypNames 순서
            public int[] hypAlias = new int[4];             // 같은 회전(0.5° 안)이면 앞 후보의 텍스처를 쓴다
            public Texture2D[] hypTex = new Texture2D[4];
            public float[][] hypErr = new float[5][];       // 후보별 정점마다 셰이더 경로 재현 오차(°) ([4] = 돌린 자세에서 다시 굽기)
            public Vector3[] Pw;                            // 정점 월드 위치 (찍는 자세)
            public bool[] cover; public Dictionary<int, Vector3> stamp;
        }

        // URP 서브셰이더의 패스 LightMode (UnityToon.shader 1068 UniversalForward, 1160 SRPDefaultUnlit(Outline), 1209 Universal2D, 1244 ShadowCaster, 1279 DepthOnly, 1317 DepthNormals)
        static readonly string[] UrpLightModes = { "UniversalForward", "SRPDefaultUnlit", "Universal2D", "ShadowCaster", "DepthOnly", "DepthNormals" };

        static Vector3 SolveTBN(Vector3 T, Vector3 B, Vector3 N, Vector3 t)
        {
            float det = Vector3.Dot(T, Vector3.Cross(B, N));
            if (Mathf.Abs(det) < 1e-4f) return new Vector3(Vector3.Dot(t, T), Vector3.Dot(t, B), Vector3.Dot(t, N));
            return new Vector3(Vector3.Dot(t, Vector3.Cross(B, N)), Vector3.Dot(T, Vector3.Cross(t, N)), Vector3.Dot(T, Vector3.Cross(B, t))) / det;
        }
        static int Wrap(int i, int n) { int r = i % n; return r < 0 ? r + n : r; }
        static float QHalf(float x) { return Mathf.HalfToFloat(Mathf.FloatToHalf(x)); }
        static float Q8(float x) { return Mathf.Round(Mathf.Clamp01(x) * 255f) / 255f; }
        static Vector3 SampleBilinear(Color[] col, int N, Vector2 uv, Func<float, float> q)
        {
            float x = uv.x * N - 0.5f, y = uv.y * N - 0.5f;
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y); float fx = x - x0, fy = y - y0;
            Func<int, int, Vector3> T = (xx, yy) => { var c = col[Wrap(yy, N) * N + Wrap(xx, N)]; return new Vector3(q(c.r), q(c.g), q(c.b)); };
            return Vector3.Lerp(Vector3.Lerp(T(x0, y0), T(x0 + 1, y0), fx), Vector3.Lerp(T(x0, y0 + 1), T(x0 + 1, y0 + 1), fx), fy);
        }
        static void Footprint(Vector2 uv, int N, List<int> ids)
        {
            ids.Clear();
            int x0 = Mathf.FloorToInt(uv.x * N - 0.5f), y0 = Mathf.FloorToInt(uv.y * N - 0.5f);
            for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++) { int id = Wrap(y0 + dy, N) * N + Wrap(x0 + dx, N); if (!ids.Contains(id)) ids.Add(id); }
        }
        static float Pct(List<float> sorted, double p) { if (sorted.Count == 0) return 0f; int k = (int)Math.Min(sorted.Count - 1, Math.Floor(p * (sorted.Count - 1) + 0.5)); return sorted[k]; }

        /// <summary>R = 렌더(오브젝트) 공간 → 월드 회전. 렌더 공간 원본 노멀(R⁻¹·NoW)을 셰이더의 월드 TBN 으로 푼 c 를 UV0 공간에 굽는다.</summary>
        static Texture2D BakeOne(BakeTarget tg, int hk, Quaternion R, string label, bool keepMaps, List<UnityEngine.Object> temps, string pngName)
        {
            int N = BakeRes, n = tg.uv.Length;
            var Rinv = Quaternion.Inverse(R);
            var tgt = new Vector3[n]; var c = new Vector3[n]; int degTBN = 0;
            for (int i = 0; i < n; i++)
            {
                tgt[i] = (Rinv * tg.NoW[i]).normalized;
                if (Mathf.Abs(Vector3.Dot(tg.Tw[i], Vector3.Cross(tg.Bw[i], tg.Nw[i]))) < 1e-4f) degTBN++;
                c[i] = SolveTBN(tg.Tw[i], tg.Bw[i], tg.Nw[i], tgt[i]).normalized;
            }
            // 1) 삼각형 래스터라이즈 (텍셀 중심, 무게중심 보간)
            var acc = new Vector3[N * N]; var cnt = new int[N * N]; var confl = new bool[N * N];
            int degTri = 0, bigTri = 0, conflTex = 0; var tris = tg.tris;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
            Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue), uvMax = new Vector2(float.MinValue, float.MinValue);
            foreach (var u in tg.uv) { uvMin = Vector2.Min(uvMin, u); uvMax = Vector2.Max(uvMax, u); }
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                Vector2 p0 = tg.uv[i0] * N, p1 = tg.uv[i1] * N, p2 = tg.uv[i2] * N;
                float area = (p1.x - p0.x) * (p2.y - p0.y) - (p2.x - p0.x) * (p1.y - p0.y);
                if (Mathf.Abs(area) < 1e-10f) { degTri++; continue; }
                float minx = Mathf.Min(p0.x, Mathf.Min(p1.x, p2.x)), maxx = Mathf.Max(p0.x, Mathf.Max(p1.x, p2.x));
                float miny = Mathf.Min(p0.y, Mathf.Min(p1.y, p2.y)), maxy = Mathf.Max(p0.y, Mathf.Max(p1.y, p2.y));
                if (maxx - minx > N || maxy - miny > N) { bigTri++; continue; }
                int x0 = Mathf.CeilToInt(minx - 0.5f), x1 = Mathf.FloorToInt(maxx - 0.5f), y0 = Mathf.CeilToInt(miny - 0.5f), y1 = Mathf.FloorToInt(maxy - 0.5f);
                bx0 = Math.Min(bx0, x0); by0 = Math.Min(by0, y0); bx1 = Math.Max(bx1, x1); by1 = Math.Max(by1, y1);
                for (int ty = y0; ty <= y1; ty++) for (int tx = x0; tx <= x1; tx++)
                {
                    float qx = tx + 0.5f, qy = ty + 0.5f;
                    float w0 = ((p2.x - p1.x) * (qy - p1.y) - (qx - p1.x) * (p2.y - p1.y)) / area;
                    float w1 = ((p0.x - p2.x) * (qy - p2.y) - (qx - p2.x) * (p0.y - p2.y)) / area;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -1e-5f || w1 < -1e-5f || w2 < -1e-5f) continue;
                    var v = c[i0] * w0 + c[i1] * w1 + c[i2] * w2;
                    if (v.sqrMagnitude < 1e-12f) continue;
                    v.Normalize();
                    int id = Wrap(ty, N) * N + Wrap(tx, N);
                    if (cnt[id] > 0 && !confl[id] && Vector3.Angle(acc[id].normalized, v) > RasterConflictDeg) { confl[id] = true; conflTex++; }
                    acc[id] += v; cnt[id]++;
                }
            }
            var filled = new bool[N * N]; var val = new Vector3[N * N]; int covered = 0, overlap = 0;
            for (int id = 0; id < N * N; id++) if (cnt[id] > 0) { filled[id] = true; val[id] = acc[id].normalized; covered++; if (cnt[id] > 1) overlap++; }
            if (keepMaps) { tg.cover = new bool[N * N]; Array.Copy(filled, tg.cover, N * N); }
            acc = null; cnt = null; confl = null;
            // 2) 번지기: 빈 텍셀에 채워진 8이웃 평균을 DilateTexels 번
            int sx0 = 0, sy0 = 0, sx1 = N - 1, sy1 = N - 1;
            if (bx0 != int.MaxValue && bx0 >= 0 && by0 >= 0 && bx1 < N && by1 < N)
            { sx0 = Math.Max(0, bx0 - DilateTexels - 2); sy0 = Math.Max(0, by0 - DilateTexels - 2); sx1 = Math.Min(N - 1, bx1 + DilateTexels + 2); sy1 = Math.Min(N - 1, by1 + DilateTexels + 2); }
            int dilated = 0;
            for (int it = 0; it < DilateTexels; it++)
            {
                var add = new List<int>(); var addV = new List<Vector3>();
                for (int y = sy0; y <= sy1; y++) for (int x = sx0; x <= sx1; x++)
                {
                    int id = y * N + x; if (filled[id]) continue;
                    Vector3 s = Vector3.zero; int k = 0;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nid = Wrap(y + dy, N) * N + Wrap(x + dx, N);
                        if (filled[nid]) { s += val[nid]; k++; }
                    }
                    if (k > 0 && s.sqrMagnitude > 1e-12f) { add.Add(id); addV.Add(s.normalized); }
                }
                for (int j = 0; j < add.Count; j++) { filled[add[j]] = true; val[add[j]] = addV[j]; }
                dilated += add.Count;
            }
            // 3) 정점 발자국: 정점 UV 의 쌍선형 2x2 텍셀에 그 정점 값을 그대로 (정점 셰이더는 정점 UV 위치만 읽는다)
            var stampSum = new Dictionary<int, Vector3>(); var stampFirst = new Dictionary<int, Vector3>(); var stampOwner = new Dictionary<int, int>();
            var conflVerts = new HashSet<int>(); var conflTexels = new HashSet<int>(); var ids = new List<int>();
            for (int i = 0; i < n; i++)
            {
                Footprint(tg.uv[i], N, ids);
                foreach (var id in ids)
                {
                    Vector3 f;
                    if (stampFirst.TryGetValue(id, out f))
                    {
                        if (Vector3.Angle(f, c[i]) > StampConflictDeg) { conflVerts.Add(i); conflVerts.Add(stampOwner[id]); conflTexels.Add(id); }
                        stampSum[id] += c[i];
                    }
                    else { stampFirst[id] = c[i]; stampSum[id] = c[i]; stampOwner[id] = i; }
                }
            }
            if (keepMaps) tg.stamp = new Dictionary<int, Vector3>();
            foreach (var kv in stampSum)
            {
                var v = kv.Value.sqrMagnitude > 1e-12f ? kv.Value.normalized : stampFirst[kv.Key];
                val[kv.Key] = v; filled[kv.Key] = true;
                if (keepMaps) tg.stamp[kv.Key] = v;
            }
            // 4) 텍스처 (RGBAHalf, 선형, 밉 없음, 쌍선형, 반복) — rgb = c·0.5+0.5 (셰이더가 ·2-1)
            var col = new Color[N * N]; var neutral = new Color(0.5f, 0.5f, 1f, 1f);
            for (int id = 0; id < N * N; id++) { if (filled[id]) { var e = val[id] * 0.5f + new Vector3(0.5f, 0.5f, 0.5f); col[id] = new Color(e.x, e.y, e.z, 1f); } else col[id] = neutral; }
            var tex = new Texture2D(N, N, TextureFormat.RGBAHalf, false, true) { name = "BakedNormal_" + tg.rB.name + "_" + label };
            tex.wrapMode = TextureWrapMode.Repeat; tex.filterMode = FilterMode.Bilinear; tex.anisoLevel = 0;
            tex.SetPixels(col); tex.Apply(false, false); temps.Add(tex);
            {
                // SetPixels 가 RGBAHalf 에 실제로 들어갔는지 되읽어 확인 (CPU 재현은 col 배열로 하므로 따로 본다)
                var back = tex.GetPixels(); float maxBack = 0f;
                for (int id = 0; id < N * N; id++) { if (!filled[id]) continue; var a = back[id]; var b = col[id]; maxBack = Mathf.Max(maxBack, Mathf.Max(Mathf.Abs(a.r - QHalf(b.r)), Mathf.Max(Mathf.Abs(a.g - QHalf(b.g)), Mathf.Abs(a.b - QHalf(b.b))))); }
                L(string.Format(CultureInfo.InvariantCulture, "    텍스처 되읽기 {0}: 형식 {1}, sRGB {2}, 밉 {3}, 채운 텍셀의 half 양자화 값과 최대 차 {4:E2}", tex.name, tex.format, tex.isDataSRGB, tex.mipmapCount, maxBack));
            }
            // 5) CPU 로 셰이더 경로 재현: 정점 UV 에서 쌍선형으로 읽고 ·2-1, 월드 TBN 으로 펴서 정규화 → 목표(렌더 공간 원본 노멀)와의 각도
            var errs = new List<float>(n); var errs8 = new List<float>(n); int e1 = 0, e5 = 0, e20 = 0;
            tg.hypErr[hk] = new float[n];
            for (int i = 0; i < n; i++)
            {
                var s = SampleBilinear(col, N, tg.uv[i], QHalf) * 2f - Vector3.one;
                var D = (s.x * tg.Tw[i] + s.y * tg.Bw[i] + s.z * tg.Nw[i]).normalized;
                float a = Vector3.Angle(D, tgt[i]); errs.Add(a); tg.hypErr[hk][i] = a; if (a > 1f) e1++; if (a > 5f) e5++; if (a > 20f) e20++;
                var s8 = SampleBilinear(col, N, tg.uv[i], Q8) * 2f - Vector3.one;
                errs8.Add(Vector3.Angle((s8.x * tg.Tw[i] + s8.y * tg.Bw[i] + s8.z * tg.Nw[i]).normalized, tgt[i]));
            }
            double sum = 0; foreach (var a in errs) sum += a; errs.Sort(); errs8.Sort();
            L(string.Format(CultureInfo.InvariantCulture,
                "  굽기 {0} [{1}] R={2}: 정점 {3}, 삼각형 {4} (면적 0 {5}, 너무 큼 {6}), UV0 범위 u {7:F3}..{8:F3} v {9:F3}..{10:F3}, 텍스처 {11}² RGBAHalf | 삼각형이 덮은 텍셀 {12} (두 번 이상 덮인 텍셀 {13} — 이웃 삼각형 경계 포함), 값이 >{14}° 다른 겹침(충돌) 텍셀 {15} | 번짐 {16}텍셀 → {17}개 추가 | 정점 발자국(2x2) 충돌: 정점 {18}개 · 텍셀 {19}개 (>{20}°) | TBN 퇴화 정점 {21} | 셰이더 경로 재현 오차(half): 평균 {22:F3}° 99% {23:F3}° 최대 {24:F2}° (>1° {25}, >5° {26}, >20° {27}) | 8비트로 저장했다면 최대 {28:F2}°",
                tg.rB.name, label, V3(R.eulerAngles), n, tris.Length / 3, degTri, bigTri, uvMin.x, uvMax.x, uvMin.y, uvMax.y, N, covered, overlap, RasterConflictDeg, conflTex, DilateTexels, dilated,
                conflVerts.Count, conflTexels.Count, StampConflictDeg, degTBN, sum / Math.Max(1, n), Pct(errs, 0.99), errs.Count > 0 ? errs[errs.Count - 1] : 0f, e1, e5, e20, errs8.Count > 0 ? errs8[errs8.Count - 1] : 0f));
            if (pngName != null && bx0 != int.MaxValue)
            {
                int cx0 = Math.Max(0, bx0 - 8), cy0 = Math.Max(0, by0 - 8), cx1 = Math.Min(N - 1, bx1 + 8), cy1 = Math.Min(N - 1, by1 + 8);
                int w = cx1 - cx0 + 1, h = cy1 - cy0 + 1; var px = new Color32[w * h];
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { var cc = col[(cy0 + y) * N + cx0 + x]; px[y * w + x] = filled[(cy0 + y) * N + cx0 + x] ? (Color32)cc : new Color32(40, 40, 40, 255); }
                var pt = new Texture2D(w, h, TextureFormat.RGBA32, false, true); pt.SetPixels32(px); pt.Apply();
                File.WriteAllBytes(Path.Combine(BakedDir, pngName), pt.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(pt);
                L("    굽기 그림 " + pngName + " (텍셀 x " + cx0 + ".." + cx1 + ", y " + cy0 + ".." + cy1 + " 아래가 0, rgb=c·0.5+0.5, 빈 텍셀 회색)");
            }
            return tex;
        }

        // 1차 실행에서 Shader.passCount·FindPassTagValue 는 Built-in 서브셰이더의 패스(FORWARD/Outline(ForwardBase)/FORWARD_DELTA/ShadowCaster)를 돌려주었다.
        // 그래서 패스를 끄고 켤 때와 기록할 때 모두 URP 서브셰이더의 LightMode 이름을 직접 쓴다.
        /// <summary>지금 자세에서 셰이더가 만들 월드 T·B·N, 원본 노멀(월드), 정점 위치를 다시 계산하고 렌더 공간 회전을 돌려준다 (정적: 트랜스폼, 스킨: 루트 본 — 1차 촬영 대조로 고른 것).</summary>
        static Quaternion RecomputeFrames(BakeTarget tg)
        {
            int n = tg.uv.Length; var uv3 = new List<Vector3>(); tg.toonMesh.GetUVs(3, uv3);
            Vector4[] T; Vector3[] N, No, P; Matrix4x4 l2w; Quaternion R;
            if (!tg.skinned)
            {
                T = tg.meshB.tangents; N = tg.meshB.normals; No = uv3.ToArray(); P = tg.meshB.vertices; l2w = tg.rB.transform.localToWorldMatrix; R = tg.rB.transform.rotation;
            }
            else
            {
                var smr = (SkinnedMeshRenderer)tg.rB;
                smr.sharedMesh = tg.meshOrig; var bo = new Mesh(); smr.BakeMesh(bo, true);
                smr.sharedMesh = tg.meshB; var bb = new Mesh(); smr.BakeMesh(bb, true);
                T = bb.tangents; N = bb.normals; No = bo.normals; P = bb.vertices; l2w = smr.transform.localToWorldMatrix;
                UnityEngine.Object.DestroyImmediate(bo); UnityEngine.Object.DestroyImmediate(bb);
                R = (smr.rootBone != null ? smr.rootBone : smr.transform).rotation;
            }
            var nrmM = l2w.inverse.transpose;
            tg.Tw = new Vector3[n]; tg.Bw = new Vector3[n]; tg.Nw = new Vector3[n]; tg.NoW = new Vector3[n]; tg.Pw = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                tg.Pw[i] = l2w.MultiplyPoint3x4(P[i]);
                tg.Tw[i] = l2w.MultiplyVector(new Vector3(T[i].x, T[i].y, T[i].z)).normalized;
                tg.Nw[i] = nrmM.MultiplyVector(N[i]).normalized;
                tg.Bw[i] = (Vector3.Cross(tg.Nw[i], tg.Tw[i]) * T[i].w).normalized;
                tg.NoW[i] = nrmM.MultiplyVector(No[i]).normalized;
            }
            return R;
        }

        static string PassList(Material m)
        {
            var parts = new List<string>();
            foreach (var lm in UrpLightModes) parts.Add(lm + "=" + (m.GetShaderPassEnabled(lm) ? "켬" : "끔"));
            return string.Join(", ", parts.ToArray());
        }
        static string ShaderSubshaders(Shader sh)
        {
            var sd = ShaderUtil.GetShaderData(sh); var parts = new List<string>();
            for (int s = 0; s < sd.SubshaderCount; s++)
            {
                var ss = sd.GetSubshader(s); var pn = new List<string>();
                for (int p = 0; p < ss.PassCount; p++) pn.Add(ss.GetPass(p).Name);
                parts.Add("서브셰이더 " + s + ": " + string.Join("/", pn.ToArray()));
            }
            var api = new List<string>(); for (int i = 0; i < sh.passCount; i++) api.Add(sh.FindPassTagValue(i, new ShaderTagId("LightMode")).name);
            return "ShaderData 서브셰이더 " + sd.SubshaderCount + "개, ActiveSubshaderIndex " + sd.ActiveSubshaderIndex + " | " + string.Join(" | ", parts.ToArray())
                + " || Shader.passCount " + sh.passCount + " (LightMode " + string.Join("/", api.ToArray()) + ")";
        }

        // 직전 (3) 과 같은 방법으로 툰 프리팹 인스턴스를 세우고 렌더러 재질만 UTS 로
        static GameObject MakeUtsToonCell(string name, string toonPath, Dictionary<string, Material[]> srcMatsByPath, Dictionary<string, bool> fbxStateByPath,
            Dictionary<Material, Material> utsOf, Dictionary<string, Mesh> fbxMeshByPath, bool log)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(toonPath));
            go.name = name;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) mb.enabled = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                r.SetPropertyBlock(null);
                var emptyBlock = new MaterialPropertyBlock();
                for (int i = 0; i < r.sharedMaterials.Length; i++) { try { r.SetPropertyBlock(null, i); } catch { r.SetPropertyBlock(emptyBlock, i); } }
                string path = RelPath(r.transform, go.transform);
                if (!srcMatsByPath.ContainsKey(path))
                {
                    string only = null; int hits = 0;
                    foreach (var kv in srcMatsByPath) { var nm = kv.Key.Substring(kv.Key.LastIndexOf('/') + 1); if (nm == r.name) { only = kv.Key; hits++; } }
                    if (hits == 1) { if (log) L("  (경로 " + path + " → 이름으로 맞춤 " + only + ")"); path = only; }
                }
                var tmesh = MeshOf(r);
                var toonMats = r.sharedMaterials; var tn = new List<string>(); foreach (var m in toonMats) tn.Add(m != null ? m.name : "null");
                bool toonOn = r.enabled && r.gameObject.activeSelf;
                Material[] src;
                if (!srcMatsByPath.TryGetValue(path, out src)) { r.enabled = false; if (log) L("  " + path + ": 원본 FBX 에 없는 렌더러 → 끔"); continue; }
                bool want = fbxStateByPath[path];
                var dst = new Material[toonMats.Length]; string mapNote = "";
                for (int i = 0; i < toonMats.Length; i++)
                {
                    Material s = i < src.Length ? src[i] : null;
                    if (src.Length != toonMats.Length && toonMats[i] != null)
                        foreach (var cand in src) if (cand != null && toonMats[i].name.StartsWith(cand.name, StringComparison.OrdinalIgnoreCase)) { s = cand; mapNote = " (칸 수가 달라 이름으로 맞춤)"; }
                    Material u; dst[i] = s != null && utsOf.TryGetValue(s, out u) ? u : null;
                }
                r.sharedMaterials = dst;
                if (toonOn != want) { r.gameObject.SetActive(want || r.gameObject.activeSelf); r.enabled = want; }
                if (log)
                {
                    var dn = new List<string>(); foreach (var m in dst) dn.Add(m != null ? m.name : "null");
                    L("  " + path + ": 툰 재질 " + string.Join(",", tn.ToArray()) + " → " + string.Join(",", dn.ToArray()) + mapNote + " | 켜짐 툰 " + toonOn + " → " + want
                      + " | " + MeshCompare(fbxMeshByPath.ContainsKey(path) ? fbxMeshByPath[path] : null, tmesh));
                }
            }
            return go;
        }

        static Vector2 ProjectPx(Bounds bb, Vector3 p, int W, int H, float margin)
        {
            float aspect = (float)W / H, tanH = Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad);
            float halfH = Mathf.Max(bb.size.y * 0.5f, bb.size.x * 0.5f / aspect);
            float dist = halfH / tanH * margin + bb.size.z * 0.5f;
            var camPos = bb.center + Vector3.forward * dist; var rot = Quaternion.LookRotation(bb.center - camPos, Vector3.up);
            var lp = Quaternion.Inverse(rot) * (p - camPos);
            float nx = lp.x / (lp.z * tanH * aspect), ny = lp.y / (lp.z * tanH);
            return new Vector2((nx * 0.5f + 0.5f) * W, (1f - (ny * 0.5f + 0.5f)) * H);   // 왼쪽 위 원점
        }

        public static void RunBaked()
        {
            Directory.CreateDirectory(BakedDir); Log.Clear();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            var temps = new List<UnityEngine.Object>();
            var cells = new List<GameObject>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var utsShader = Shader.Find("Toon/Toon");
                L("UTS 셰이더 " + (utsShader != null ? utsShader.name + " (" + AssetDatabase.GetAssetPath(utsShader) + ") 지원=" + utsShader.isSupported : "없음"));
                if (utsShader == null) return;
                L("셰이더 코드(읽음) Runtime/Shaders/URP/UniversalToonOutline.hlsl: 35 N=UnityObjectToWorldNormal(v.normal) | 36 T=normalize(ObjectToWorld·tangent.xyz) | 37 B=normalize(cross(N,T)·tangent.w) | 38 float3x3(T,B,N) 행 | 40 tex2Dlod(_BakedNormal, TRANSFORM_TEX(uv0), mip 0)·2-1 | 41 dir=normalize(mul(rgb, TBN)) = 월드 | 57-60 _OUTLINE_NML 일 때만 lerp(v.vertex+v.normal·w, v.vertex+dir·w, _Is_BakedNormal) (오브젝트 공간에 더함). _OUTLINE_POS 는 Baked Normal 을 안 쓴다. UnityToon.shader 1182: #pragma multi_compile _OUTLINE_NML _OUTLINE_POS, 204-205: _Is_BakedNormal [Toggle(_)] (키워드 없음), _BakedNormal 2D 기본 white");
                try { L("셰이더 구조: " + ShaderSubshaders(utsShader)); } catch (Exception ex) { L("셰이더 구조 읽기 실패: " + ex.Message); }
                L("렌더 파이프라인: " + (GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name + " (" + GraphicsSettings.currentRenderPipeline.GetType().Name + ")" : "없음"));
                string mp = FindModel(ModelName); L("모델 " + mp);
                var crit = FBX2NPRRuntimeCheck.Crit(ModelName);
                string toonPath = FBX2NPRRuntimeCheck.PrefabPath(ModelName + "_Toon"); L("툰 프리팹 " + toonPath);
                L("색 공간 " + QualitySettings.activeColorSpace);

                FBX2NPRRuntimeCheck.NewStage(false);
                var sun = RenderSettings.sun;
                L("디렉셔널 라이트: 회전 " + V3(sun.transform.rotation.eulerAngles) + ", 빛이 오는 쪽 " + V3(-sun.transform.forward) + ", 세기 " + F(sun.intensity) + ", 색 " + C3(sun.color) + ", 그림자 " + sun.shadows);
                L("주변광: 모드 " + RenderSettings.ambientMode + ", 색(저장값 sRGB) " + C3(RenderSettings.ambientLight) + ", 세기 " + F(RenderSettings.ambientIntensity));

                // ---- (D) = 직전 (2): FBX + UTS 임시 재질
                var utsFbx = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));
                utsFbx.name = "cellD_uts_orignormal"; cells.Add(utsFbx);
                var rig0 = AssetDatabase.LoadAssetAtPath<GameObject>(toonPath).GetComponentInChildren<ToonFaceRig>(true);
                Color skinShade = rig0 != null ? rig0.skinShadow : ToonFaceRig.DefaultSkinShadow;
                Color bodyShade = rig0 != null ? rig0.bodyShadowColor : ToonFaceRig.DefaultSkinShadow;
                var utsOf = new Dictionary<Material, Material>();
                var srcMatsByPath = new Dictionary<string, Material[]>();
                var fbxStateByPath = new Dictionary<string, bool>();
                var fbxMeshByPath = new Dictionary<string, Mesh>();
                L("(D) 재질 교체 (직전 (2) 와 같은 MakeUts):");
                foreach (var r in utsFbx.GetComponentsInChildren<Renderer>(true))
                {
                    string path = RelPath(r.transform, utsFbx.transform);
                    var mesh = MeshOf(r);
                    if (mesh != null && crit.IsIgnoredMesh(mesh.name)) { r.enabled = false; L("  끔(데칼 메시): " + path); }
                    var src = r.sharedMaterials; srcMatsByPath[path] = src; fbxMeshByPath[path] = mesh;
                    fbxStateByPath[path] = r.enabled && r.gameObject.activeSelf;
                    var dst = new Material[src.Length];
                    for (int i = 0; i < src.Length; i++)
                    {
                        if (src[i] == null) continue;
                        Material u;
                        if (!utsOf.TryGetValue(src[i], out u))
                        {
                            var tex = src[i].mainTexture != null ? src[i].mainTexture : FBX2NPRConverter.ResolveAlbedo(src[i], mp, crit);
                            var kind = KindOf(src[i].name, crit);
                            u = MakeUts(src[i], tex, kind, kind == Kind.FaceSkin ? skinShade : bodyShade, temps);
                            utsOf[src[i]] = u;
                        }
                        dst[i] = u;
                    }
                    r.sharedMaterials = dst;
                }
                Bounds head; int hv;
                if (!FBX2NPRParseBake.HeadBounds(utsFbx, crit, out head, out hv)) head = FBX2NPRRuntimeCheck.HeadOf(utsFbx, ModelName, new List<string>());
                L("머리 바운드: 중심 " + V3(head.center) + " 크기 " + V3(head.size) + " 정점 " + hv + "  (직전 실험 값: 중심 (-0.0086, 1.3857, -0.0256) 크기 (0.2779, 0.2591, 0.2458) 정점 2646)");

                // ---- (A)(B)(C): 툰 프리팹 인스턴스 셋, 직전 (3) 과 같은 재질 교체
                L("(A) 재질 교체·켜짐 맞춤 (직전 (3) 과 같은 방법):");
                var cellA = MakeUtsToonCell("cellA_uts_toonkitnormal", toonPath, srcMatsByPath, fbxStateByPath, utsOf, fbxMeshByPath, true); cells.Add(cellA);
                var cellB = MakeUtsToonCell("cellB_uts_bakednormal", toonPath, srcMatsByPath, fbxStateByPath, utsOf, fbxMeshByPath, false); cells.Add(cellB);
                var cellC = MakeUtsToonCell("cellC_reference", toonPath, srcMatsByPath, fbxStateByPath, utsOf, fbxMeshByPath, false); cells.Add(cellC);
                Func<GameObject, Dictionary<string, Renderer>> byPath = g => { var d = new Dictionary<string, Renderer>(); foreach (var r in g.GetComponentsInChildren<Renderer>(true)) d[RelPath(r.transform, g.transform)] = r; return d; };
                var rendB = byPath(cellB); var rendC = byPath(cellC);

                // ---- 대상 메시: 정리된 노멀이 원본(UV3)과 다른, 외곽선이 켜진 UTS 재질을 쓰는 렌더러
                var targets = new List<BakeTarget>();
                L("대상 찾기 (툰 메시 normals vs UV3(원본 노멀) — FBX2NPRConverter.cs 163-166: UV3·탄젠트에 원본 노멀):");
                foreach (var r in cellA.GetComponentsInChildren<Renderer>(true))
                {
                    var m = MeshOf(r); if (m == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    var uv3 = new List<Vector3>(); m.GetUVs(3, uv3);
                    if (uv3.Count != m.vertexCount) continue;
                    var nn = m.normals; var tt = m.tangents; double sumA = 0; float maxA = 0, maxTan = 0; int nDiff = 0, n20 = 0;
                    for (int i = 0; i < nn.Length; i++)
                    {
                        float a = Vector3.Angle(nn[i], uv3[i]); sumA += a; if (a > maxA) maxA = a; if (a > 0.1f) nDiff++; if (a > 20f) n20++;
                        if (tt.Length == nn.Length) maxTan = Mathf.Max(maxTan, Vector3.Angle(new Vector3(tt[i].x, tt[i].y, tt[i].z), uv3[i]));
                    }
                    string path = RelPath(r.transform, cellA.transform);
                    Material uts = null; foreach (var sm in r.sharedMaterials) if (sm != null && sm.shader == utsShader) { uts = sm; break; }
                    bool outlineOn = uts != null && uts.GetShaderPassEnabled("SRPDefaultUnlit") && uts.GetFloat("_Outline_Width") > 0f;
                    if (nDiff == 0) continue;
                    // 원본 FBX 메시와 정점 순서 대조
                    string fbxNote = "FBX 메시 없음";
                    Mesh fm; if (fbxMeshByPath.TryGetValue(path, out fm) && fm != null)
                    {
                        if (fm.vertexCount != m.vertexCount) fbxNote = "FBX 정점 수 다름 " + fm.vertexCount;
                        else
                        {
                            var fp = fm.vertices; var fn = fm.normals; var tp = m.vertices; float dp = 0f, dn = 0f, dnCleaned = 0f;
                            for (int i = 0; i < fp.Length; i++) { dp = Mathf.Max(dp, (fp[i] - tp[i]).magnitude); dn = Mathf.Max(dn, Vector3.Angle(fn[i], uv3[i])); dnCleaned = Mathf.Max(dnCleaned, Vector3.Angle(fn[i], nn[i])); }
                            fbxNote = string.Format(CultureInfo.InvariantCulture, "FBX 메시와 같은 순서: 위치 차 최대 {0:E1}, FBX 노멀 vs UV3 최대 {1:F4}°, FBX 노멀 vs 정리 노멀 최대 {2:F1}°", dp, dn, dnCleaned);
                        }
                    }
                    L(string.Format(CultureInfo.InvariantCulture, "  {0} ({1}, 메시 {2} v{3}, 서브메시 {4}) 재질 {5} 외곽선 {6}: 정리 vs 원본 노멀 평균 {7:F1}° 최대 {8:F1}° (0.1° 넘음 {9}, 20° 넘음 {10}), 탄젠트 채널 vs UV3 최대 {11:F4}° | {12}",
                        path, r is SkinnedMeshRenderer ? "skinned" : "static", m.name, m.vertexCount, m.subMeshCount, uts != null ? uts.name : "UTS 아님", outlineOn ? "켬" : "끔",
                        sumA / Math.Max(1, nn.Length), maxA, nDiff, n20, maxTan, fbxNote));
                    if (!outlineOn) { L("    → 외곽선이 꺼진 재질이라 제외"); continue; }
                    Renderer rb, rc; if (!rendB.TryGetValue(path, out rb) || !rendC.TryGetValue(path, out rc)) { L("    → (B)(C) 인스턴스에서 못 찾음, 제외"); continue; }
                    var tg = new BakeTarget { path = path, skinned = r is SkinnedMeshRenderer, rA = r, rB = rb, rC1 = rc, toonMesh = m, uts = uts };
                    tg.meshB = UnityEngine.Object.Instantiate(m); tg.meshB.name = m.name + "_utsB_realTangent"; temps.Add(tg.meshB);
                    tg.meshB.RecalculateTangents();
                    tg.meshOrig = UnityEngine.Object.Instantiate(m); tg.meshOrig.name = m.name + "_origNormal"; temps.Add(tg.meshOrig);
                    tg.meshOrig.SetNormals(uv3); tg.meshOrig.RecalculateTangents();
                    tg.uv = tg.meshB.uv; tg.tris = tg.meshB.triangles;
                    {
                        var T = tg.meshB.tangents; var N = tg.meshB.normals; float maxDot = 0f; int neg = 0, zeroT = 0;
                        for (int i = 0; i < T.Length; i++) { var t3 = new Vector3(T[i].x, T[i].y, T[i].z); if (t3.sqrMagnitude < 1e-8f) zeroT++; maxDot = Mathf.Max(maxDot, Mathf.Abs(Vector3.Dot(t3.normalized, N[i].normalized))); if (T[i].w < 0f) neg++; }
                        L(string.Format(CultureInfo.InvariantCulture, "    (B) 메시 사본 {0}: normals=정리 노멀 그대로, RecalculateTangents() → |T·N| 최대 {1:F5}, w<0 정점 {2}, 길이 0 탄젠트 {3}, UV0 {4}개",
                            tg.meshB.name, maxDot, neg, zeroT, tg.uv.Length));
                    }
                    targets.Add(tg);
                }
                if (targets.Count == 0) { L("대상 없음 — 중단"); return; }

                // ---- (B) 메시 교체 후 셰이더가 쓸 월드 TBN·원본 노멀 (정적: 트랜스폼, 스킨: 지금 자세로 BakeMesh)
                foreach (var tg in targets)
                {
                    int n = tg.uv.Length;
                    tg.Tw = new Vector3[n]; tg.Bw = new Vector3[n]; tg.Nw = new Vector3[n]; tg.NoW = new Vector3[n];
                    var uv3 = new List<Vector3>(); tg.toonMesh.GetUVs(3, uv3);
                    Vector4[] T; Vector3[] N, No, P; Matrix4x4 l2w;
                    if (!tg.skinned)
                    {
                        tg.rB.GetComponent<MeshFilter>().sharedMesh = tg.meshB;
                        T = tg.meshB.tangents; N = tg.meshB.normals; No = uv3.ToArray(); P = tg.meshB.vertices; l2w = tg.rB.transform.localToWorldMatrix;
                        tg.hypR[0] = Quaternion.identity; tg.hypR[1] = tg.rB.transform.rotation; tg.hypR[2] = tg.hypR[1]; tg.hypR[3] = tg.rB.localToWorldMatrix.rotation;
                        L("  " + tg.rB.name + " (정적) 트랜스폼 회전 " + V3(tg.rB.transform.rotation.eulerAngles) + " (단위와 " + F(Quaternion.Angle(Quaternion.identity, tg.rB.transform.rotation)) + "°), lossyScale " + V3(tg.rB.transform.lossyScale)
                          + ", Renderer.localToWorldMatrix 회전과 트랜스폼 회전 차 " + F(Quaternion.Angle(tg.hypR[3], tg.hypR[1])) + "°");
                    }
                    else
                    {
                        var smr = (SkinnedMeshRenderer)tg.rB;
                        smr.sharedMesh = tg.meshOrig; var bo = new Mesh(); smr.BakeMesh(bo, true);
                        smr.sharedMesh = tg.meshB; var bb = new Mesh(); smr.BakeMesh(bb, true);
                        T = bb.tangents; N = bb.normals; No = bo.normals; P = bb.vertices; l2w = smr.transform.localToWorldMatrix;
                        L("  " + smr.name + " (스킨) BakeMesh: 탄젠트 " + T.Length + " 노멀 " + N.Length + " 원본 노멀 " + No.Length + " / 정점 " + n);
                        UnityEngine.Object.DestroyImmediate(bo); UnityEngine.Object.DestroyImmediate(bb);
                        var root = smr.rootBone != null ? smr.rootBone : smr.transform;
                        tg.hypR[0] = Quaternion.identity; tg.hypR[1] = root.rotation; tg.hypR[2] = smr.transform.rotation; tg.hypR[3] = smr.localToWorldMatrix.rotation;
                        for (int k = 0; k < 4; k++) L("    렌더 공간 후보 " + k + " " + SkinHypNames[k] + (k == 1 ? " (" + root.name + ")" : "") + ": 회전 " + V3(tg.hypR[k].eulerAngles) + " (단위와 " + F(Quaternion.Angle(Quaternion.identity, tg.hypR[k])) + "°)");
                    }
                    if (T.Length != n || N.Length != n || No.Length != n) { L("    정점 배열 길이가 맞지 않음 — 이 대상은 굽지 못함"); continue; }
                    var nrmM = l2w.inverse.transpose;
                    tg.Pw = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        tg.Pw[i] = l2w.MultiplyPoint3x4(P[i]);
                        tg.Tw[i] = l2w.MultiplyVector(new Vector3(T[i].x, T[i].y, T[i].z)).normalized;
                        tg.Nw[i] = nrmM.MultiplyVector(N[i]).normalized;
                        tg.Bw[i] = (Vector3.Cross(tg.Nw[i], tg.Tw[i]) * T[i].w).normalized;
                        tg.NoW[i] = nrmM.MultiplyVector(No[i]).normalized;
                    }
                    for (int k = 0; k < 4; k++) { tg.hypAlias[k] = k; for (int j = 0; j < k; j++) if (Quaternion.Angle(tg.hypR[j], tg.hypR[k]) < 0.5f) { tg.hypAlias[k] = tg.hypAlias[j]; break; } }
                }

                // ---- 굽기: 후보마다 (같은 회전이면 하나만)
                L("굽기 (해상도 " + BakeRes + ", 정점 발자국 덮어쓰기, 번짐 " + DilateTexels + "텍셀):");
                foreach (var tg in targets)
                {
                    if (tg.Tw == null || tg.NoW == null || tg.NoW.Length == 0) continue;
                    // 주 굽기: 정적 = 트랜스폼 회전(후보 1, 단위 회전이면 후보 0 과 같은 텍스처), 스킨 = 루트 본 회전(후보 1) — 스킨 쪽은 아래에서 촬영으로 다시 고른다
                    int mainK = tg.hypAlias[1];
                    for (int k = 0; k < 4; k++)
                    {
                        if (tg.hypAlias[k] != k) { tg.hypTex[k] = tg.hypTex[tg.hypAlias[k]]; tg.hypErr[k] = tg.hypErr[tg.hypAlias[k]]; continue; }
                        string label = tg.skinned ? "후보" + k + " " + SkinHypNames[k] : (k == 0 ? (tg.hypAlias[1] == 0 ? "보정 없음 = 보정(트랜스폼 회전이 단위)" : "보정 없음(교과서식 탄젠트 공간)") : "보정(트랜스폼 회전)");
                        bool keepMaps = k == mainK;
                        string png = keepMaps ? "bake_" + tg.rB.name + ".png" : null;
                        tg.hypTex[k] = BakeOne(tg, k, tg.hypR[k], label, keepMaps, temps, png);
                    }
                }
                // 같은 재질을 쓰는 메시끼리 UV 충돌
                L("같은 UTS 재질을 쓰는 메시끼리 UV 충돌 (주 굽기 기준):");
                for (int a = 0; a < targets.Count; a++) for (int b = a + 1; b < targets.Count; b++)
                {
                    var ta = targets[a]; var tb = targets[b];
                    if (ta.uts != tb.uts || ta.cover == null || tb.cover == null) continue;
                    int both = 0; for (int id = 0; id < ta.cover.Length; id++) if (ta.cover[id] && tb.cover[id]) both++;
                    int sBoth = 0, sConf = 0; foreach (var kv in ta.stamp) { Vector3 vb; if (tb.stamp.TryGetValue(kv.Key, out vb)) { sBoth++; if (Vector3.Angle(kv.Value, vb) > StampConflictDeg) sConf++; } }
                    L("  " + ta.rB.name + " × " + tb.rB.name + " (" + ta.uts.name + "): 둘 다 덮은 텍셀 " + both + ", 정점 발자국이 겹친 텍셀 " + sBoth + " 중 값이 >" + StampConflictDeg + "° 다른 것 " + sConf);
                }
                L("  → 메시마다 재질 사본·텍스처를 따로 만든다 (같은 재질을 나눠 쓰지 않음)");

                // ---- (B) 재질: 대상마다 UTS 재질 사본 + _Is_BakedNormal=1 + 구운 텍스처
                foreach (var tg in targets)
                {
                    if (tg.hypTex[1] == null && tg.hypTex[0] == null) continue;
                    tg.matB = new Material(tg.uts) { name = tg.uts.name + "_B_" + tg.rB.name }; temps.Add(tg.matB);
                    tg.matB.SetFloat("_Is_BakedNormal", 1f);
                    tg.matB.SetTexture("_BakedNormal", tg.hypTex[1] ?? tg.hypTex[0]);
                    tg.matB.SetTextureScale("_BakedNormal", Vector2.one); tg.matB.SetTextureOffset("_BakedNormal", Vector2.zero);
                    tg.matB.SetShaderPassEnabled("SRPDefaultUnlit", true);
                    var ms = tg.rB.sharedMaterials; for (int i = 0; i < ms.Length; i++) if (ms[i] == tg.uts) ms[i] = tg.matB; tg.rB.sharedMaterials = ms;
                    L("  (B) " + tg.rB.name + ": 재질 " + tg.matB.name + " _Is_BakedNormal " + F(tg.matB.GetFloat("_Is_BakedNormal")) + " _OUTLINE " + F(tg.matB.GetFloat("_OUTLINE")) + " 키워드 _OUTLINE_NML " + tg.matB.IsKeywordEnabled("_OUTLINE_NML") + " _OUTLINE_POS " + tg.matB.IsKeywordEnabled("_OUTLINE_POS")
                      + " 외곽선 폭 " + F(tg.matB.GetFloat("_Outline_Width")) + " | 패스 " + PassList(tg.matB));
                }

                // ---- (C) 기준: 정리 노멀 렌더러(외곽선 끔) + 같은 자리 원본 노멀 렌더러(외곽선만)
                foreach (var tg in targets)
                {
                    if (tg.matB == null) continue;
                    var srcSlots = tg.rC1.sharedMaterials;
                    tg.matC1 = new Material(tg.uts) { name = tg.uts.name + "_C1_" + tg.rC1.name }; temps.Add(tg.matC1);
                    tg.matC1.SetShaderPassEnabled("SRPDefaultUnlit", false);
                    tg.matC2 = new Material(tg.uts) { name = tg.uts.name + "_C2_" + tg.rC1.name }; temps.Add(tg.matC2);
                    foreach (var lm in UrpLightModes) tg.matC2.SetShaderPassEnabled(lm, lm == "SRPDefaultUnlit");
                    var matNone = new Material(tg.matC2) { name = "none" }; temps.Add(matNone);
                    foreach (var lm in UrpLightModes) matNone.SetShaderPassEnabled(lm, false);
                    var c1 = new Material[srcSlots.Length]; var c2 = new Material[srcSlots.Length]; int other = 0;
                    for (int i = 0; i < srcSlots.Length; i++) { bool isT = srcSlots[i] == tg.uts; c1[i] = isT ? tg.matC1 : srcSlots[i]; c2[i] = isT ? tg.matC2 : matNone; if (!isT) other++; }
                    tg.rC1.sharedMaterials = c1;
                    var go2 = new GameObject(tg.rC1.name + "_origNormalOutline");
                    go2.transform.SetParent(tg.rC1.transform.parent, false);
                    go2.transform.localPosition = tg.rC1.transform.localPosition; go2.transform.localRotation = tg.rC1.transform.localRotation; go2.transform.localScale = tg.rC1.transform.localScale;
                    go2.layer = tg.rC1.gameObject.layer;
                    Renderer r2;
                    if (tg.skinned)
                    {
                        var s1 = (SkinnedMeshRenderer)tg.rC1; var s2 = go2.AddComponent<SkinnedMeshRenderer>();
                        s2.sharedMesh = tg.meshOrig; s2.bones = s1.bones; s2.rootBone = s1.rootBone; s2.localBounds = s1.localBounds;
                        s2.quality = s1.quality; s2.updateWhenOffscreen = s1.updateWhenOffscreen; s2.skinnedMotionVectors = s1.skinnedMotionVectors;
                        for (int k = 0; k < tg.meshOrig.blendShapeCount; k++) s2.SetBlendShapeWeight(k, s1.GetBlendShapeWeight(k));
                        r2 = s2;
                    }
                    else { go2.AddComponent<MeshFilter>().sharedMesh = tg.meshOrig; r2 = go2.AddComponent<MeshRenderer>(); }
                    r2.shadowCastingMode = tg.rC1.shadowCastingMode; r2.receiveShadows = tg.rC1.receiveShadows; r2.lightProbeUsage = tg.rC1.lightProbeUsage;
                    r2.reflectionProbeUsage = tg.rC1.reflectionProbeUsage; r2.probeAnchor = tg.rC1.probeAnchor; r2.renderingLayerMask = tg.rC1.renderingLayerMask;
                    r2.sortingLayerID = tg.rC1.sortingLayerID; r2.sortingOrder = tg.rC1.sortingOrder; r2.motionVectorGenerationMode = tg.rC1.motionVectorGenerationMode;
                    r2.sharedMaterials = c2; tg.rC2 = r2;
                    L("  (C) " + tg.rC1.name + ": 정리 노멀 렌더러 재질 " + tg.matC1.name + " [" + PassList(tg.matC1) + "] + " + go2.name + " (메시 " + tg.meshOrig.name + ", " + (tg.skinned ? "스킨, 같은 본·루트 본" : "정적, 같은 부모·로컬 TRS") + ") 재질 " + tg.matC2.name + " [" + PassList(tg.matC2) + "]" + (other > 0 ? " (다른 재질 칸 " + other + "개는 패스 없는 재질)" : ""));
                }

                // ---- 입 영역: MTH_DEF 정점을 촬영 카메라로 투영한 사각형 + 12px
                int mx0 = 0, my0 = 0, mx1 = 0, my1 = 0;
                {
                    Renderer mth = null; foreach (var r in cellA.GetComponentsInChildren<Renderer>(true)) if (r.name == "MTH_DEF") mth = r;
                    if (mth != null)
                    {
                        var mv = MeshOf(mth).vertices; float minx = 1e9f, miny = 1e9f, maxx = -1e9f, maxy = -1e9f;
                        foreach (var v in mv) { var p = ProjectPx(head, mth.transform.TransformPoint(v), 700, 700, 1.18f); minx = Mathf.Min(minx, p.x); maxx = Mathf.Max(maxx, p.x); miny = Mathf.Min(miny, p.y); maxy = Mathf.Max(maxy, p.y); }
                        mx0 = Mathf.Max(0, Mathf.FloorToInt(minx) - 12); my0 = Mathf.Max(0, Mathf.FloorToInt(miny) - 12); mx1 = Mathf.Min(700, Mathf.CeilToInt(maxx) + 12); my1 = Mathf.Min(700, Mathf.CeilToInt(maxy) + 12);
                        L(string.Format(CultureInfo.InvariantCulture, "참고: MTH_DEF 정점 {0}개 투영 x {1:F1}..{2:F1} y {3:F1}..{4:F1} — MTH_DEF 는 입만이 아니라 얼굴 앞면 전체라 입 영역으로 쓰지 않는다 (1차 실행에서 확인)", mv.Length, minx, maxx, miny, maxy));
                    }
                    // 입 영역은 직접 정한다: 1차 실행 (A) 그림에서 입 검은 덩어리가 x 287..375 y 534..563, (D) 의 입선·입꼬리 점이 x 297..372 y 537..550 → 둘을 덮고 턱선(y≈580~)에 닿지 않게
                    mx0 = 270; mx1 = 395; my0 = 515; my1 = 580;
                    L("입 영역(직접 정함): x [" + mx0 + "," + mx1 + ") y [" + my0 + "," + my1 + ") — 700px 그림, 왼쪽 위 원점");
                    File.WriteAllText(Path.Combine(BakedDir, "mouth_rect.txt"), mx0 + " " + my0 + " " + mx1 + " " + my1);
                }

                // ---- 재현 오차가 큰 정점: 어디에 있고(화면 px), 왜 (같은 UV 를 쓰는 다른 정점이 다른 값을 원함)
                foreach (var tg in targets)
                {
                    if (tg.Pw == null) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        if (tg.hypAlias[k] != k || tg.hypErr[k] == null) continue;
                        if (!tg.skinned && k != tg.hypAlias[1]) continue;
                        var err = tg.hypErr[k]; int n5 = 0, inFrame = 0, inMouth = 0; var samples = new List<string>();
                        var nn = tg.meshB.normals; var uv3 = new List<Vector3>(); tg.toonMesh.GetUVs(3, uv3);
                        for (int i = 0; i < err.Length; i++)
                        {
                            if (err[i] <= 5f) continue;
                            n5++;
                            var p = ProjectPx(head, tg.Pw[i], 700, 700, 1.18f);
                            bool fr = p.x >= 0 && p.x < 700 && p.y >= 0 && p.y < 700; if (fr) inFrame++;
                            bool mo = p.x >= mx0 && p.x < mx1 && p.y >= my0 && p.y < my1; if (mo) inMouth++;
                            if (samples.Count < 8 && fr)
                            {
                                // 같은 UV(0.75 텍셀 안)의 다른 정점
                                int partner = -1; float pd = 0f;
                                for (int j = 0; j < tg.uv.Length; j++) { if (j == i) continue; if ((tg.uv[j] - tg.uv[i]).magnitude * BakeRes < 0.75f) { partner = j; pd = (tg.Pw[j] - tg.Pw[i]).magnitude; break; } }
                                samples.Add(string.Format(CultureInfo.InvariantCulture, "v{0} 화면({1:F0},{2:F0}) UV({3:F4},{4:F4}) 오차 {5:F1}° 정리-원본 {6:F0}°{7}", i, p.x, p.y, tg.uv[i].x, tg.uv[i].y, err[i], Vector3.Angle(nn[i], uv3[i]),
                                    partner >= 0 ? string.Format(CultureInfo.InvariantCulture, " | 같은 UV 정점 v{0} (거리 {1:F1}mm, 정리-원본 {2:F0}°)", partner, pd * 1000f, Vector3.Angle(nn[partner], uv3[partner])) : " | 같은 UV 정점 없음"));
                            }
                        }
                        L(string.Format(CultureInfo.InvariantCulture, "재현 오차 >5° 정점 {0} [{1}]: {2}개, 화면 안 {3}, 입 영역 안 {4}{5}", tg.rB.name, tg.skinned ? "후보" + k + " " + SkinHypNames[k] : "주 굽기", n5, inFrame, inMouth,
                            samples.Count > 0 ? " — 예: " + string.Join("; ", samples.ToArray()) : ""));
                    }
                }

                // ---- 찍기
                Action<GameObject, string> shoot = (go, file) =>
                {
                    foreach (var g in cells) g.SetActive(g == go);
                    go.SetActive(false); go.SetActive(true);
                    if (feat != null) feat.SetActive(false);
                    FBX2NPRCompare.SetSS(2);
                    FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(BakedDir, file), Vector3.forward, false, 1.18f);
                };
                string warm = "_warmup.png";
                foreach (var g in new[] { utsFbx, cellA, cellB, cellC }) shoot(g, warm);
                if (File.Exists(Path.Combine(BakedDir, warm))) File.Delete(Path.Combine(BakedDir, warm));
                L("버리는 촬영 D·A·B·C 한 번씩 마침");
                shoot(utsFbx, "unitychan_D_uts_orignormal.png");
                shoot(cellA, "unitychan_A_uts_toonkitnormal.png");
                shoot(cellC, "unitychan_C_reference.png");
                L("찍음 D, A, C (외곽선 피처 " + (feat != null && feat.isActive) + ")");

                // (B) 스킨 메시 렌더 공간 후보별 — 정적 메시는 보정(트랜스폼) 그대로
                Action<int, int> setB = (staticK, skinK) => { foreach (var tg in targets) if (tg.matB != null) tg.matB.SetTexture("_BakedNormal", tg.hypTex[tg.skinned ? skinK : staticK]); };
                var skinTargets = targets.FindAll(t => t.skinned && t.matB != null);
                int bestK = 1; long bestDiff = long.MaxValue;
                var shotKs = new List<int>();
                for (int k = 0; k < 4; k++)
                {
                    bool distinct = skinTargets.Count == 0 ? k == 1 : skinTargets.Exists(t => t.hypAlias[k] == k);
                    if (!distinct) continue;
                    shotKs.Add(k);
                    setB(1, k);
                    string f = "unitychan_B_skinhyp" + k + ".png";
                    shoot(cellB, f);
                    long d = CountDiff(Path.Combine(BakedDir, f), Path.Combine(BakedDir, "unitychan_C_reference.png"), 24);
                    L("찍음 " + f + " (정적 메시 보정, 스킨 메시 후보 " + k + " " + SkinHypNames[k] + "): 기준(C)과 색이 다른 화소(RGB 차 합 >24) " + d);
                    if (d < bestDiff) { bestDiff = d; bestK = k; }
                }
                L("스킨 메시 렌더 공간: 기준(C)과 가장 가까운 후보 " + bestK + " " + SkinHypNames[bestK] + (skinTargets.Count == 0 ? " (스킨 대상 없음)" : ""));
                setB(0, 0);
                shoot(cellB, "unitychan_B_naive_tangentspace.png");
                L("찍음 unitychan_B_naive_tangentspace.png (모든 메시 보정 없음 = 교과서식 탄젠트 공간 굽기): 기준(C)과 다른 화소 " + CountDiff(Path.Combine(BakedDir, "unitychan_B_naive_tangentspace.png"), Path.Combine(BakedDir, "unitychan_C_reference.png"), 24));
                setB(1, bestK);
                shoot(cellB, "unitychan_B_uts_baked.png");
                L("찍음 unitychan_B_uts_baked.png (주: 정적 보정 + 스킨 후보 " + bestK + "), 같은 설정의 후보 촬영과 다른 화소(차 >0) " + CountDiff(Path.Combine(BakedDir, "unitychan_B_uts_baked.png"), Path.Combine(BakedDir, "unitychan_B_skinhyp" + bestK + ".png"), 0));

                // ---- 외곽선을 끈 같은 칸
                var allUts = new List<Material>(utsOf.Values); foreach (var tg in targets) { if (tg.matB != null) allUts.Add(tg.matB); if (tg.matC1 != null) allUts.Add(tg.matC1); }
                var was = new List<bool>(); foreach (var m in allUts) was.Add(m.GetShaderPassEnabled("SRPDefaultUnlit"));
                foreach (var m in allUts) m.SetShaderPassEnabled("SRPDefaultUnlit", false);
                foreach (var tg in targets) if (tg.rC2 != null) tg.rC2.enabled = false;
                shoot(utsFbx, "unitychan_D_off.png"); shoot(cellA, "unitychan_A_off.png"); shoot(cellB, "unitychan_B_off.png"); shoot(cellC, "unitychan_C_off.png");
                for (int i = 0; i < allUts.Count; i++) allUts[i].SetShaderPassEnabled("SRPDefaultUnlit", was[i]);
                foreach (var tg in targets) if (tg.rC2 != null) tg.rC2.enabled = true;
                L("찍음 외곽선 끈 칸 A·B·C·D (모든 UTS 재질의 SRPDefaultUnlit 끔, C 의 원본 노멀 렌더러 끔)");
                // (A) 다시 찍기 — 찍는 순서 영향
                shoot(cellA, "unitychan_A_again.png");
                L("다시 찍음 A: 처음과 다른 화소(차 >0) " + CountDiff(Path.Combine(BakedDir, "unitychan_A_again.png"), Path.Combine(BakedDir, "unitychan_A_uts_toonkitnormal.png"), 0));

                // ---- 보조: 캐릭터 전체를 머리 중심 기준으로 Y 30° 돌린 자세 (텍스처는 돌리기 전에 구운 그대로, 카메라·라이트는 그대로)
                // 셰이더가 월드 TBN 으로 편 방향을 오브젝트 공간 위치에 더하므로(UniversalToonOutline.hlsl 36·41·59-60), 렌더 공간의 월드 회전이 굽던 때와 달라지면 미는 방향이 그만큼 돈다 — 그것이 화면에 드러나는지 본다.
                {
                    var qy = Quaternion.Euler(0f, 30f, 0f);
                    var rotCells = new[] { cellA, cellB, cellC };
                    var keepPos = new Vector3[3]; var keepRot = new Quaternion[3];
                    for (int i = 0; i < 3; i++) { keepPos[i] = rotCells[i].transform.position; keepRot[i] = rotCells[i].transform.rotation; rotCells[i].transform.rotation = qy * keepRot[i]; rotCells[i].transform.position = head.center + qy * (keepPos[i] - head.center); }
                    foreach (var tg in targets) if (tg.rB != null) L("  돌린 자세 " + tg.rB.name + ": 렌더러 트랜스폼 회전 " + V3(tg.rB.transform.rotation.eulerAngles) + (tg.skinned && ((SkinnedMeshRenderer)tg.rB).rootBone != null ? ", 루트 본 회전 " + V3(((SkinnedMeshRenderer)tg.rB).rootBone.rotation.eulerAngles) : ""));
                    shoot(cellA, "unitychan_A_rot30.png"); shoot(cellB, "unitychan_B_rot30.png"); shoot(cellC, "unitychan_C_rot30.png");
                    foreach (var m in allUts) m.SetShaderPassEnabled("SRPDefaultUnlit", false);
                    foreach (var tg in targets) if (tg.rC2 != null) tg.rC2.enabled = false;
                    shoot(cellC, "unitychan_C_rot30_off.png");
                    for (int i = 0; i < allUts.Count; i++) allUts[i].SetShaderPassEnabled("SRPDefaultUnlit", was[i]);
                    foreach (var tg in targets) if (tg.rC2 != null) tg.rC2.enabled = true;
                    Func<string, int> dark = f => { int w, h; var px = LoadAbs(Path.Combine(BakedDir, f), out w, out h); int c = 0; foreach (var p in px) if (Luma(p) < 80f) c++; return c; };
                    L(string.Format(CultureInfo.InvariantCulture, "돌린 자세(Y 30°, 머리 중심): 그림 전체 어두운 화소(밝기<80, 배경 포함) — 외곽선 끈 C {0}, A {1}, B {2}, C {3} | B vs C 다른 화소(RGB 차 합 >24) {4}, A vs C {5}",
                        dark("unitychan_C_rot30_off.png"), dark("unitychan_A_rot30.png"), dark("unitychan_B_rot30.png"), dark("unitychan_C_rot30.png"),
                        CountDiff(Path.Combine(BakedDir, "unitychan_B_rot30.png"), Path.Combine(BakedDir, "unitychan_C_rot30.png"), 24), CountDiff(Path.Combine(BakedDir, "unitychan_A_rot30.png"), Path.Combine(BakedDir, "unitychan_C_rot30.png"), 24)));
                    // 같은 자세에서 CPU 로 셰이더 경로를 다시 따라가 본 오차: 렌더 공간 회전이 qy 만큼 바뀌었을 때 (정적 메시: 단순히 R → qy·R)
                    foreach (var tg in targets)
                    {
                        if (tg.skinned || tg.hypTex[1] == null) continue;
                        var tex = tg.hypTex[tg.hypAlias[1]]; var col = tex.GetPixels(); int n = tg.uv.Length; var uv3 = new List<Vector3>(); tg.toonMesh.GetUVs(3, uv3);
                        double sum = 0; float mx = 0f;
                        for (int i = 0; i < n; i++)
                        {
                            var s = SampleBilinear(col, BakeRes, tg.uv[i], x => x) * 2f - Vector3.one;
                            var Tn = qy * tg.Tw[i]; var Bn = qy * tg.Bw[i]; var Nn = qy * tg.Nw[i];
                            var D = (s.x * Tn + s.y * Bn + s.z * Nn).normalized;
                            var want = (Quaternion.Inverse(qy * tg.hypR[1]) * (qy * tg.NoW[i])).normalized;
                            float a = Vector3.Angle(D, want); sum += a; mx = Mathf.Max(mx, a);
                        }
                        L(string.Format(CultureInfo.InvariantCulture, "  CPU 재현 {0}: 돌린 자세에서 미는 방향 오차 평균 {1:F1}° 최대 {2:F1}° (돌리기 전 주 굽기 오차와 비교)", tg.rB.name, sum / Math.Max(1, n), mx));
                    }
                    // 원인 확인: 돌린 자세에서 다시 구우면 기준(C)과 다시 맞는가
                    foreach (var g in rotCells) { g.SetActive(true); g.SetActive(false); g.SetActive(true); }
                    foreach (var tg in targets)
                    {
                        if (tg.matB == null || tg.Pw == null) continue;
                        var oldPw = tg.Pw;
                        var Rn = RecomputeFrames(tg);
                        float dev = 0f; for (int i = 0; i < oldPw.Length; i++) dev = Mathf.Max(dev, (tg.Pw[i] - (head.center + qy * (oldPw[i] - head.center))).magnitude);
                        L(string.Format(CultureInfo.InvariantCulture, "  다시 굽기 {0}: 렌더 공간 회전 {1}, 정점 위치가 '머리 중심 Y 30° 회전'과 어긋난 최대 거리 {2:E1} m", tg.rB.name, V3(Rn.eulerAngles), dev));
                        var tex = BakeOne(tg, 4, Rn, "돌린 자세에서 다시 굽기", false, temps, null);
                        tg.matB.SetTexture("_BakedNormal", tex);
                    }
                    shoot(cellB, "unitychan_B_rot30_rebaked.png");
                    L(string.Format(CultureInfo.InvariantCulture, "돌린 자세에서 다시 구운 B: 어두운 화소 {0} | 기준(C) 과 다른 화소 {1}",
                        dark("unitychan_B_rot30_rebaked.png"), CountDiff(Path.Combine(BakedDir, "unitychan_B_rot30_rebaked.png"), Path.Combine(BakedDir, "unitychan_C_rot30.png"), 24)));
                    setB(1, bestK);
                    for (int i = 0; i < 3; i++) { rotCells[i].transform.rotation = keepRot[i]; rotCells[i].transform.position = keepPos[i]; }
                    foreach (var g in rotCells) { g.SetActive(false); g.SetActive(true); }
                    shoot(cellB, "unitychan_B_check_after_rot.png");
                    L("자세를 되돌린 뒤 B 다시 찍기: 처음 B 와 다른 화소(차 >0) " + CountDiff(Path.Combine(BakedDir, "unitychan_B_check_after_rot.png"), Path.Combine(BakedDir, "unitychan_B_uts_baked.png"), 0));
                }

                // (E) FBX2NPR 툰: 직전 이미지 재사용
                File.Copy(Path.Combine(CompareDir, "unitychan_4_toonkit.png"), Path.Combine(BakedDir, "unitychan_E_toonkit.png"), true);
                L("(E) FBX2NPR 툰: 직전 실험 이미지 uts_compare/unitychan_4_toonkit.png 를 unitychan_E_toonkit.png 로 복사 (다시 찍지 않음)");

                AnalyzeCore();
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                foreach (var g in cells) if (g != null) UnityEngine.Object.DestroyImmediate(g);
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                FBX2NPRCompare.SetSS(1);
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                File.WriteAllText(Path.Combine(BakedDir, "log_uts_baked.txt"), string.Join("\n", Log.ToArray()), new UTF8Encoding(true));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        // ================= 측정 =================
        static Color32[] LoadAbs(string path, out int w, out int h)
        {
            var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(path));
            w = t.width; h = t.height; var px = t.GetPixels32(); UnityEngine.Object.DestroyImmediate(t); return px;
        }
        static int SumDiff(Color32 a, Color32 b) { return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b); }
        static float Luma(Color32 c) { return 0.299f * c.r + 0.587f * c.g + 0.114f * c.b; }
        static long CountDiff(string fa, string fb, int thr)
        {
            int w, h, w2, h2; var a = LoadAbs(fa, out w, out h); var b = LoadAbs(fb, out w2, out h2);
            if (w != w2 || h != h2) return -1;
            long n = 0; for (int i = 0; i < a.Length; i++) if (SumDiff(a[i], b[i]) > thr) n++; return n;
        }

        struct Region { public string name; public int x0, y0, x1, y1; public bool exclMouth; }

        public static void AnalyzeBaked()
        {
            Log.Clear();
            try { AnalyzeCore(); } catch (Exception ex) { L("예외: " + ex); }
            File.WriteAllText(Path.Combine(BakedDir, "log_uts_baked_analyze.txt"), string.Join("\n", Log.ToArray()), new UTF8Encoding(true));
        }

        static void AnalyzeCore()
        {
            var mr = File.ReadAllText(Path.Combine(BakedDir, "mouth_rect.txt")).Split(' ');
            int mx0 = int.Parse(mr[0]), my0 = int.Parse(mr[1]), mx1 = int.Parse(mr[2]), my1 = int.Parse(mr[3]);
            const int W = 700, H = 700;
            Func<int, int, bool> inMouth = (x, yt) => x >= mx0 && x < mx1 && yt >= my0 && yt < my1;
            var regions = new List<Region>
            {
                new Region { name = "전체", x0 = 0, y0 = 0, x1 = W, y1 = H, exclMouth = false },
                new Region { name = "입", x0 = mx0, y0 = my0, x1 = mx1, y1 = my1, exclMouth = false },
                // 영역은 1차 실행 (A) 그림에 50px 격자를 얹어 보고 정했다: 얼굴 윤곽선은 (185,400)→(270,575)→턱 끝(330,600)→(400,570)→(490,400)
                new Region { name = "얼굴 아래(입 제외 전부)", x0 = 175, y0 = 380, x1 = 505, y1 = 610, exclMouth = true },
                new Region { name = "턱선(입 제외)", x0 = 250, y0 = 560, x1 = 420, y1 = 610, exclMouth = true },
                new Region { name = "화면 왼쪽 뺨 윤곽", x0 = 175, y0 = 400, x1 = 275, y1 = 575, exclMouth = true },
                new Region { name = "화면 오른쪽 뺨 윤곽", x0 = 400, y0 = 400, x1 = 500, y1 = 575, exclMouth = true },
                new Region { name = "목·어깨", x0 = 150, y0 = 610, x1 = 550, y1 = 700, exclMouth = false },
                new Region { name = "머리카락 왼쪽", x0 = 0, y0 = 120, x1 = 175, y1 = 610, exclMouth = false },
                new Region { name = "머리카락 오른쪽", x0 = 505, y0 = 120, x1 = 700, y1 = 610, exclMouth = false },
                new Region { name = "머리 위(머리카락·리본)", x0 = 90, y0 = 20, x1 = 610, y1 = 140, exclMouth = false },
            };
            L("측정 영역 (700x700, 왼쪽 위 원점, [x0,x1) [y0,y1)): " + string.Join("; ", regions.ConvertAll(r => r.name + " x " + r.x0 + ".." + r.x1 + " y " + r.y0 + ".." + r.y1 + (r.exclMouth ? " 입 제외" : "")).ToArray()));

            var img = new Dictionary<string, Color32[]>();
            Func<string, Color32[]> get = key =>
            {
                Color32[] px; if (img.TryGetValue(key, out px)) return px;
                string p = key.StartsWith("old:") ? Path.Combine(CompareDir, key.Substring(4)) : Path.Combine(BakedDir, key);
                if (!File.Exists(p)) { L("  없음: " + p); return null; }
                int w, h; px = LoadAbs(p, out w, out h); if (w != W || h != H) { L("  크기 다름: " + p); return null; }
                img[key] = px; return px;
            };
            Func<int, int, int> idx = (x, yt) => (H - 1 - yt) * W + x;   // PNG 를 LoadImage 하면 0 행이 아래

            // 재현 확인
            Action<string, string, string> exact = (fa, fb, what) =>
            {
                var a = get(fa); var b = get(fb); if (a == null || b == null) return;
                int any = 0, big = 0, maxd = 0; for (int i = 0; i < a.Length; i++) { int d = SumDiff(a[i], b[i]); if (d > 0) any++; if (d > 24) big++; maxd = Math.Max(maxd, d); }
                L(string.Format("재현·동일성 {0}: 다른 화소 {1} (RGB 차 합 >24 {2}), 최대 차 {3}", what, any, big, maxd));
            };
            exact("unitychan_A_uts_toonkitnormal.png", "old:unitychan_3_uts_toonkitnormal.png", "(A) vs 직전 3번 칸");
            exact("unitychan_D_uts_orignormal.png", "old:unitychan_2_uts_orignormal.png", "(D) vs 직전 2번 칸");
            exact("unitychan_A_off.png", "old:unitychan_3_uts_toonkitnormal_nooutline.png", "(A) 외곽선 끔 vs 직전 3번 외곽선 끔");
            exact("unitychan_D_off.png", "old:unitychan_2_uts_orignormal_nooutline.png", "(D) 외곽선 끔 vs 직전 2번 외곽선 끔");
            exact("unitychan_B_off.png", "unitychan_A_off.png", "(B) 외곽선 끔 vs (A) 외곽선 끔 (탄젠트 재계산이 본 패스에 영향?)");
            exact("unitychan_C_off.png", "unitychan_A_off.png", "(C) 외곽선 끔 vs (A) 외곽선 끔");

            // 입: 어두운 화소 (밝기 0.299R+0.587G+0.114B < 80)
            var cellsOn = new[] { "A", "B", "C", "D" };
            var label = new Dictionary<string, string> { { "A", "UTS · FBX2NPR 노멀" }, { "B", "UTS · FBX2NPR 노멀 + 외곽선용 원본 노멀" }, { "C", "기준(외곽선만 원본 노멀 메시)" }, { "D", "UTS · 원본 노멀" } };
            var onFile = new Dictionary<string, string> { { "A", "unitychan_A_uts_toonkitnormal.png" }, { "B", "unitychan_B_uts_baked.png" }, { "C", "unitychan_C_reference.png" }, { "D", "unitychan_D_uts_orignormal.png" } };
            L("입 영역 x [" + mx0 + "," + mx1 + ") y [" + my0 + "," + my1 + ") 화소 " + ((mx1 - mx0) * (my1 - my0)) + " — 어두운 화소(밝기<80):");
            foreach (var k in cellsOn)
            {
                var on = get(onFile[k]); var off = get("unitychan_" + k + "_off.png"); if (on == null || off == null) continue;
                int dOn = 0, dOff = 0, added = 0, removed = 0; double lOn = 0, lOff = 0; int cnt = 0;
                for (int yt = my0; yt < my1; yt++) for (int x = mx0; x < mx1; x++)
                {
                    var a = on[idx(x, yt)]; var b = off[idx(x, yt)]; float la = Luma(a), lb = Luma(b);
                    bool da = la < 80f, db = lb < 80f; if (da) dOn++; if (db) dOff++; if (da && !db) added++; if (!da && db) removed++;
                    lOn += la; lOff += lb; cnt++;
                }
                L(string.Format(CultureInfo.InvariantCulture, "  ({0}) {1}: 외곽선 켬 {2}, 끈 같은 칸 {3}, 외곽선이 더한 어두운 화소 {4} (없앤 것 {5}) | 평균 밝기 켬 {6:F1} 끔 {7:F1}", k, label[k], dOn, dOff, added, removed, lOn / cnt, lOff / cnt));
            }
            // 외곽선 화소 (켬 vs 끔: RGB 차 합 >24 이고 켬이 더 어두움) — 영역별
            L("외곽선 화소 (켬과 끈 같은 칸이 RGB 차 합 >24 로 다르고 켬이 더 어두운 화소) 영역별:");
            foreach (var k in cellsOn)
            {
                var on = get(onFile[k]); var off = get("unitychan_" + k + "_off.png"); if (on == null || off == null) continue;
                var parts = new List<string>();
                foreach (var rg in regions)
                {
                    int n = 0;
                    for (int yt = rg.y0; yt < rg.y1; yt++) for (int x = rg.x0; x < rg.x1; x++)
                    {
                        if (rg.exclMouth && inMouth(x, yt)) continue;
                        var a = on[idx(x, yt)]; var b = off[idx(x, yt)];
                        if (SumDiff(a, b) > 24 && Luma(a) < Luma(b)) n++;
                    }
                    parts.Add(rg.name + " " + n);
                }
                L("  (" + k + ") " + string.Join(", ", parts.ToArray()));
            }
            // 칸끼리 차이 — 영역별
            L("칸끼리 다른 화소 (RGB 차 합 >24) 영역별:");
            var pairs = new[] { new[] { "B", "C" }, new[] { "B", "A" }, new[] { "B", "D" }, new[] { "C", "D" }, new[] { "A", "C" }, new[] { "Bnaive", "C" } };
            foreach (var pr in pairs)
            {
                Func<string, string> fileOf = s => s == "Bnaive" ? "unitychan_B_naive_tangentspace.png" : onFile[s];
                var a = get(fileOf(pr[0])); var b = get(fileOf(pr[1])); if (a == null || b == null) continue;
                var parts = new List<string>(); int maxd = 0;
                foreach (var rg in regions)
                {
                    int n = 0;
                    for (int yt = rg.y0; yt < rg.y1; yt++) for (int x = rg.x0; x < rg.x1; x++)
                    {
                        if (rg.exclMouth && inMouth(x, yt)) continue;
                        int d = SumDiff(a[idx(x, yt)], b[idx(x, yt)]); if (d > 24) n++; if (rg.name == "전체") maxd = Math.Max(maxd, d);
                    }
                    parts.Add(rg.name + " " + n);
                }
                L("  " + pr[0] + " vs " + pr[1] + ": " + string.Join(", ", parts.ToArray()) + " | 전체 최대 차 " + maxd);
            }
            WriteDiff(onFile["B"], onFile["C"], "unitychan_diff_B_vs_C.png");
            WriteDiff(onFile["A"], onFile["B"], "unitychan_diff_A_vs_B.png");
            WriteDiff(onFile["B"], onFile["D"], "unitychan_diff_B_vs_D.png");
        }

        static void WriteDiff(string fa, string fb, string outName)
        {
            int w, h, w2, h2; var a = LoadAbs(Path.Combine(BakedDir, fa), out w, out h); var b = LoadAbs(Path.Combine(BakedDir, fb), out w2, out h2);
            if (w != w2 || h != h2) return;
            var o = new Color32[a.Length]; int n = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int d = SumDiff(a[i], b[i]); byte gy = (byte)((a[i].r + a[i].g + a[i].b) / 3);
                if (d > 24) { n++; o[i] = new Color32(255, 40, 40, 255); } else o[i] = new Color32((byte)(gy / 3 + 20), (byte)(gy / 3 + 20), (byte)(gy / 3 + 20), 255);
            }
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false); t.SetPixels32(o); t.Apply();
            File.WriteAllBytes(Path.Combine(BakedDir, outName), t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
            L("차이 그림 " + outName + " (" + fa + " vs " + fb + "): 빨강 = RGB 차 합 >24, " + n + "화소");
        }
    }
}
