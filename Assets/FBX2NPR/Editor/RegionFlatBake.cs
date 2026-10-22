// RegionFlatBake.cs
// RegionFlatCore(색 무리별 평탄화)를 유니티 메시·텍스처에 연결합니다.
//   1) 이 텍스처를 쓰는 메시의 삼각형을 UV 공간에 그려, 텍셀마다 '어느 메시가 덮는가'와 얼굴 가중치를 얻습니다.
//   2) 얼굴 평탄화가 이미 끝난 텍셀(얼굴 렌더러의 얼굴 가중치 0.5 이상)은 손대지 않습니다.
//   3) 메시마다 기준 길이(눈 사이 거리의 20%)를 그 메시의 텍셀 밀도로 환산해 넘깁니다.
// 원본 에셋과 임포트 설정은 건드리지 않습니다. 결과는 새 텍스처(메모리)로 돌려주고, 저장은 호출한 쪽이 합니다.
using System.Collections.Generic;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class RegionFlatBake
    {
        public class Src
        {
            public string name; public Mesh mesh; public Matrix4x4 l2w;
            public float[] faceW;       // 정점별 얼굴 가중치 (얼굴 렌더러가 아니면 null 가능)
            public bool faceRend;       // 얼굴 재질을 받는 렌더러인가 (그 얼굴 가중치 0.5 이상 텍셀은 얼굴 평탄화가 맡았다)
            public bool skinRule;       // 재질·메시 이름이 피부 규칙에 맞는가 (실제로 피부인지는 색으로 다시 가립니다)
            public bool skip;           // 눈·이·속눈썹·머리카락처럼 손대면 안 되는 메시
            public bool clothName;      // 메시 이름이 옷인가 (그러면 피부로 보지 않는다)
            public int changed;         // (결과) 이 메시에서 바뀐 텍셀 수
        }

        public class Result
        {
            public Texture2D albedo;    // 결과 (바뀐 것이 없으면 null)
            public Texture2D mask;      // 등급 마스크 (RegionFlatCore.Run 의 mask 설명 참고)
            public List<string> log = new List<string>();
            public int changedTexels;
        }

        /// <param name="start">출발 텍스처. 얼굴 평탄화본(메모리)이나 원본 알베도</param>
        /// <param name="like">원본 알베도 (크기와 감싸기·필터 설정을 따릅니다)</param>
        /// <param name="ipd">눈 사이 거리 (미터)</param>
        const float DoneWeight = 0.98f;

        public static Result Bake(Texture start, Texture like, List<Src> parts, float ipd, bool haveFill, Color32 fill, RegionFlatCore.Opt o)
        {
            return Bake(start, like, parts, ipd, haveFill, fill, o, null);
        }
        /// <param name="doneOverride">텍셀별 '끝난 곳'(얼굴 파싱 라벨이 맡은 텍셀, 텍스처 크기). 있으면 얼굴 가중치 기준(DoneWeight)을 쓰지 않는다 — 가중치 경계에서 귀가 잘리던 문제(76번)가 라벨 경로에서는 생기지 않는다</param>
        public static Result Bake(Texture start, Texture like, List<Src> parts, float ipd, bool haveFill, Color32 fill, RegionFlatCore.Opt o, bool[] doneOverride)
        {
            var res = new Result();
            int W = like.width, H = like.height, N = W * H;
            Color32[] px = Read(start, W, H);
            var id = new byte[N]; var done = new bool[N];
            var infos = new List<RegionFlatCore.MeshInfo>();

            // 얼굴 렌더러를 마지막에 그려, 겹치는 텍셀에서는 얼굴 쪽이 이기게 합니다.
            var order = new List<Src>(parts);
            order.Sort((a, b) => (a.faceRend ? 1 : 0).CompareTo(b.faceRend ? 1 : 0));
            foreach (var p in parts) p.changed = 0;
            // 메시 번호는 한 바이트다. 넘으면 얼굴 렌더러(맨 뒤)가 잘려 얼굴 평탄화 결과를 덮어쓸 수 있으므로 아예 하지 않는다.
            if (order.Count > 255) { res.log.Add("이 텍스처를 쓰는 메시가 255개를 넘어 몸 평탄화를 하지 않음"); return res; }
            var srcOf = new List<Src>();
            foreach (var p in order)
            {
                var v = p.mesh.vertices; var uv = p.mesh.uv; var tri = p.mesh.triangles;
                if (uv == null || uv.Length != v.Length) { res.log.Add("메시 " + p.name + ": UV 없음"); continue; }
                srcOf.Add(p);
                byte idv = (byte)(infos.Count + 1);
                double sumTex = 0, sumWorld = 0;
                bool useW = p.faceRend && p.faceW != null && p.faceW.Length == v.Length;
                for (int t = 0; t < tri.Length; t += 3)
                {
                    int i0 = tri[t], i1 = tri[t + 1], i2 = tri[t + 2];
                    Vector2 a = uv[i0], b = uv[i1], c = uv[i2];
                    // 타일 경계 밖 UV 는 삼각형 단위로 한 칸씩 당깁니다 (SkinFlatBake 와 같은 규칙)
                    var off = new Vector2(Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), Mathf.Floor(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                    a -= off; b -= off; c -= off;
                    if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) > 1.0001f || Mathf.Max(a.y, Mathf.Max(b.y, c.y)) > 1.0001f) continue;
                    float ax = a.x * W, ay = a.y * H, bx = b.x * W, by = b.y * H, cx = c.x * W, cy = c.y * H;
                    float det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                    float texArea = 0.5f * Mathf.Abs(det); if (texArea < 1e-6f) continue;
                    var P0 = p.l2w.MultiplyPoint3x4(v[i0]); var P1 = p.l2w.MultiplyPoint3x4(v[i1]); var P2 = p.l2w.MultiplyPoint3x4(v[i2]);
                    sumTex += texArea; sumWorld += 0.5f * Vector3.Cross(P1 - P0, P2 - P0).magnitude;
                    float w0 = useW ? p.faceW[i0] : 0f, w1 = useW ? p.faceW[i1] : 0f, w2 = useW ? p.faceW[i2] : 0f;
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
                        // 얼굴 평탄화가 온전히 맡은 텍셀(가중치 1 에 가까운 곳)만 '끝난 곳'으로 둡니다. 가중치 0.5~1 구간은 얼굴 평탄화가 가중치만큼만 섞어 놓아
                        // 그대로 두면 양쪽 평탄색 사이에 어두운 띠로 남습니다(턱 밑). 이 구간은 몸 평탄화가 다시 고릅니다.
                        bool fin = w >= DoneWeight;
                        if (done[i] && !fin) continue;                       // 겹치면 얼굴 쪽(처리 끝난 쪽)을 지킵니다 (다른 얼굴 렌더러와 겹쳐도)
                        id[i] = idv; done[i] = fin;
                    }
                }
                var mi = new RegionFlatCore.MeshInfo();
                mi.id = idv; mi.name = p.name; mi.skinRule = p.skinRule; mi.skip = p.skip; mi.clothName = p.clothName;
                float rho = sumWorld > 1e-10 ? (float)System.Math.Sqrt(sumTex / sumWorld) : 0f;
                mi.hwTex = Mathf.Max(2f, 0.2f * ipd * rho);
                infos.Add(mi);
            }

            if (doneOverride != null && doneOverride.Length == N) { for (int i = 0; i < N; i++) done[i] = doneOverride[i]; res.log.Add("끝난 곳: 얼굴 파싱 라벨이 맡은 텍셀 기준"); }
            var rgba = new byte[N * 4];
            for (int i = 0; i < N; i++) { rgba[i * 4] = px[i].r; rgba[i * 4 + 1] = px[i].g; rgba[i * 4 + 2] = px[i].b; rgba[i * 4 + 3] = px[i].a; }
            var maskB = new byte[N * 4];
            var outB = RegionFlatCore.Run(rgba, W, H, id, done, infos.ToArray(), haveFill, fill.r, fill.g, fill.b, o, res.log, maskB);
            for (int k = 0; k < infos.Count && k < srcOf.Count; k++) srcOf[k].changed = infos[k].changed;
            var outPx = new Color32[N]; var mk = new Color32[N]; int changed = 0;
            for (int i = 0; i < N; i++)
            {
                outPx[i] = new Color32(outB[i * 4], outB[i * 4 + 1], outB[i * 4 + 2], outB[i * 4 + 3]);
                mk[i] = new Color32(maskB[i * 4], maskB[i * 4 + 1], maskB[i * 4 + 2], 255);
                if (outB[i * 4] != rgba[i * 4] || outB[i * 4 + 1] != rgba[i * 4 + 1] || outB[i * 4 + 2] != rgba[i * 4 + 2]) changed++;
            }
            res.changedTexels = changed;
            var mt = new Texture2D(W, H, TextureFormat.RGBA32, false, true); mt.filterMode = FilterMode.Point; mt.SetPixels32(mk); mt.Apply(false, false); res.mask = mt;
            if (changed > 0)
            {
                var t = new Texture2D(W, H, TextureFormat.RGBA32, true, false);   // sRGB
                t.wrapMode = like.wrapMode; t.filterMode = like.filterMode; t.anisoLevel = like.anisoLevel;
                t.SetPixels32(outPx); t.Apply(true, false); res.albedo = t;
            }
            return res;
        }

        /// <summary>읽을 수 있는 메모리 텍스처는 그대로, 임포트된 텍스처(읽기 불가·압축)는 GPU 를 거쳐 sRGB 8비트로 읽습니다. 행 0 = 아래.</summary>
        public static Color32[] Read(Texture src, int W, int H)
        {
            var t2 = src as Texture2D;
            if (t2 != null && t2.isReadable && t2.width == W && t2.height == H) return t2.GetPixels32();
            var rt = RenderTexture.GetTemporary(W, H, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, W, H), 0, 0); t.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = t.GetPixels32(); Object.DestroyImmediate(t); return px;
        }
    }
}
