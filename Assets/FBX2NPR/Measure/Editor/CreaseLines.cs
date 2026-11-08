using System;
using System.Collections.Generic;
using UnityEngine;

namespace FBX2NPR
{
    /// <summary>
    /// 면이 꺾이는 모서리 위에 얇은 띠 메시를 만듭니다.
    ///
    /// 왜 필요한가: 얼굴 노멀을 평탄화하면 귀 같은 부위가 음영에서 통째로 사라집니다.
    /// 측정으로 확인했습니다 — 귀를 평탄화에서 빼면 오히려 얼룩이 두 배가 되고,
    /// 그대로 두면 형태가 없어집니다. 사라진 형태는 음영으로 되살릴 수 없습니다.
    /// 그래서 애니메이션 얼굴이 하는 대로, 형태를 선이 담당하게 합니다.
    ///
    /// 선은 지오메트리라 조명과 무관합니다. 빛이 돌아도 형태가 흔들리지 않습니다.
    /// </summary>
    public static class CreaseLines
    {
        /// <summary>
        /// 꺾임 모서리에 띠를 깐 메시를 만듭니다. 만들 것이 없으면 null.
        /// </summary>
        /// <param name="creaseAngleDeg">두 면 사이 각도가 이 값 이상이면 선을 긋습니다. 30도가 적절했습니다.</param>
        /// <param name="minLengthRatio">모델 크기 대비 최소 모서리 길이. 0.009 정도.
        /// 이 필터가 없으면 콧구멍 안쪽의 잔 모서리가 전부 잡혀 까만 덩어리가 됩니다.</param>
        /// <param name="widthRatio">모델 크기 대비 선 굵기.</param>
        public static Mesh Build(Mesh src, float creaseAngleDeg, float minLengthRatio,
                                 float widthRatio, out int lineCount)
        {
            lineCount = 0;
            if (src == null) return null;

            Vector3[] pos = src.vertices;
            int[] tri = src.triangles;
            if (pos.Length == 0 || tri.Length == 0) return null;

            Vector3 bs = src.bounds.size;
            // 대각선이 아니라 최대 변을 기준으로 삼습니다.
            // 블렌더 프로토타입에서 구한 비율(0.009)과 같은 기준이어야 값을 그대로 옮길 수 있습니다.
            float size = Mathf.Max(bs.x, Mathf.Max(bs.y, bs.z));
            float minLen = size * minLengthRatio;
            float halfW  = size * widthRatio * 0.5f;
            float lift   = size * 0.0008f;   // 표면에서 살짝 띄웁니다

            // 유니티는 노멀·UV가 갈라지는 자리에서 정점을 쪼갭니다.
            // 인덱스로 모서리를 묶으면 인접 면을 못 찾으므로 좌표로 묶습니다.
            var canon = new Dictionary<long, int>(pos.Length);
            var cid = new int[pos.Length];
            float q = Mathf.Max(size * 1e-5f, 1e-7f);
            for (int i = 0; i < pos.Length; i++)
            {
                long key = Key(pos[i], q);
                int id;
                if (!canon.TryGetValue(key, out id)) { id = canon.Count; canon[key] = id; }
                cid[i] = id;
            }

            // 모서리마다 인접 면의 법선을 최대 2개까지 모읍니다.
            var edges = new Dictionary<long, Edge>(tri.Length);
            for (int t = 0; t < tri.Length; t += 3)
            {
                int a = tri[t], b = tri[t + 1], c = tri[t + 2];
                Vector3 fn = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                if (fn.sqrMagnitude < 1e-20f) continue;   // 퇴화 삼각형
                fn.Normalize();
                Add(edges, cid, a, b, fn);
                Add(edges, cid, b, c, fn);
                Add(edges, cid, c, a, fn);
            }

            var verts = new List<Vector3>();
            var tris  = new List<int>();
            var srcIdx = new List<int>();   // 본 가중치를 베껴올 원본 정점

            float cosLimit = Mathf.Cos(creaseAngleDeg * Mathf.Deg2Rad);
            foreach (var kv in edges)
            {
                Edge e = kv.Value;
                if (e.count != 2) continue;                       // 경계·비다양체 모서리는 건너뜁니다
                if (Vector3.Dot(e.n0, e.n1) > cosLimit) continue; // 충분히 꺾이지 않음

                Vector3 p0 = pos[e.v0], p1 = pos[e.v1];
                Vector3 d = p1 - p0;
                float len = d.magnitude;
                if (len < minLen || len < 1e-9f) continue;
                d /= len;

                Vector3 nAvg = (e.n0 + e.n1).normalized;
                Vector3 side = Vector3.Cross(d, nAvg);
                if (side.sqrMagnitude < 1e-12f) continue;
                side.Normalize();

                Vector3 off = nAvg * lift, hw = side * halfW;
                int b0 = verts.Count;
                verts.Add(p0 - hw + off); srcIdx.Add(e.v0);
                verts.Add(p0 + hw + off); srcIdx.Add(e.v0);
                verts.Add(p1 + hw + off); srcIdx.Add(e.v1);
                verts.Add(p1 - hw + off); srcIdx.Add(e.v1);
                tris.Add(b0); tris.Add(b0 + 1); tris.Add(b0 + 2);
                tris.Add(b0); tris.Add(b0 + 2); tris.Add(b0 + 3);
                lineCount++;
            }

            if (lineCount == 0) return null;

            var m = new Mesh();
            m.name = src.name + "_Lines";
            m.indexFormat = verts.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            m.SetVertices(verts);
            m.SetTriangles(tris, 0);

            // 스킨 메시라면 선도 같이 움직여야 합니다. 원본 정점의 가중치를 그대로 베낍니다.
            var bw = src.boneWeights;
            if (bw != null && bw.Length == pos.Length)
            {
                var nbw = new BoneWeight[verts.Count];
                for (int i = 0; i < verts.Count; i++) nbw[i] = bw[srcIdx[i]];
                m.boneWeights = nbw;
                m.bindposes = src.bindposes;
            }
            m.RecalculateBounds();
            return m;
        }

        struct Edge
        {
            public int v0, v1;        // 대표 정점(좌표가 같은 것 중 아무거나)
            public Vector3 n0, n1;
            public int count;
        }

        static long Key(Vector3 p, float q)
        {
            long x = (long)Mathf.Round(p.x / q);
            long y = (long)Mathf.Round(p.y / q);
            long z = (long)Mathf.Round(p.z / q);
            return x * 73856093L ^ y * 19349663L ^ z * 83492791L;
        }

        static void Add(Dictionary<long, Edge> map, int[] cid, int a, int b, Vector3 fn)
        {
            int ca = cid[a], cb = cid[b];
            if (ca == cb) return;
            long key = ca < cb ? ((long)ca << 32) | (uint)cb : ((long)cb << 32) | (uint)ca;
            Edge e;
            if (!map.TryGetValue(key, out e))
            {
                e.v0 = a; e.v1 = b; e.n0 = fn; e.n1 = Vector3.zero; e.count = 1;
            }
            else
            {
                if (e.count == 1) { e.n1 = fn; e.count = 2; }
                else e.count = 3;   // 세 면이 붙은 모서리는 제외합니다
            }
            map[key] = e;
        }
    }
}