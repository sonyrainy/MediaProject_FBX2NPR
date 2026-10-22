using System;
using System.Collections.Generic;
using UnityEngine;

namespace FBX2NPR
{
    /// <summary>
    /// 정점 노멀을 한 점 기준 방사형으로 정렬합니다.
    ///
    /// 블렌더의 Normal Edit 모디파이어 RADIAL 모드와 같은 일을 엔진 안에서 합니다.
    /// 굳이 유니티에서 다시 구현하는 이유는, DCC 왕복이 조용히 망가뜨리는 것들이 있기 때문입니다.
    /// 실제로 이 프로젝트에서 왕복 때문에 면 와인딩이 10% 뒤집혔고, 스케일이 100배가 됐고,
    /// 좌표계 손방향이 반대로 왔습니다. 전부 임포트 시점에 처리하면 생기지 않는 문제입니다.
    ///
    /// 같은 값을 내는지는 믿지 말고 재야 합니다. NormalOracle 이 블렌더 결과와 대조합니다.
    /// </summary>
    public static class RadialNormals
    {
        /// <summary>
        /// 정점 위치의 산술 평균. 블렌더에서 Empty 를 놓았던 그 지점입니다.
        ///
        /// 반드시 중복 위치를 걸러야 합니다. 유니티는 노멀/UV 가 갈라지는 자리에서 정점을 쪼개므로,
        /// 같은 좌표가 여러 번 들어 있습니다. 그대로 평균 내면 쪼개진 곳이 과대 대표되어
        /// 중심이 밀립니다. 실제로 이것 때문에 mix=1 에서도 블렌더와 최대 1.1도 어긋났습니다.
        /// (유니티 5383개 vs 블렌더 5300개)
        /// </summary>
        public static Vector3 Centroid(Vector3[] verts, int[] subset = null)
        {
            var seen = new HashSet<long>();
            var sum = Vector3.zero;
            int n = 0;
            int count = subset != null ? subset.Length : verts.Length;

            for (int k = 0; k < count; k++)
            {
                Vector3 v = verts[subset != null ? subset[k] : k];
                // 1e-5 격자로 양자화해 같은 좌표를 한 번만 셉니다.
                long key = (long)Mathf.Round(v.x * 100000f) * 73856093L
                         ^ (long)Mathf.Round(v.y * 100000f) * 19349663L
                         ^ (long)Mathf.Round(v.z * 100000f) * 83492791L;
                if (!seen.Add(key)) continue;
                sum += v; n++;
            }
            return n > 0 ? sum / n : Vector3.zero;
        }

        /// <summary>
        /// 구면 선형 보간. 블렌더가 노멀을 섞을 때 쓰는 방식입니다.
        ///
        /// Lerp 후 정규화로 구현했다가 정답지와 중간 구간에서 최대 34도 어긋났습니다.
        /// 두 방식은 t=0 과 t=1 에서만 같고, 사이에서는 두 벡터의 사잇각이 클수록 크게 갈라집니다.
        /// 이 메시에는 사잇각이 161도까지 있어서 차이가 그대로 드러났습니다.
        /// </summary>
        static Vector3 SlerpSafe(Vector3 a, Vector3 b, float t)
        {
            float d = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
            float theta = Mathf.Acos(d);
            // 거의 같은 방향이면 각도가 0 이라 나눗셈이 터집니다. 선형으로 충분합니다.
            if (theta < 1e-4f) return Vector3.Lerp(a, b, t).normalized;
            // 정확히 반대 방향이면 경로가 무수히 많습니다. 목표 쪽을 택합니다.
            if (theta > Mathf.PI - 1e-4f) return t < 0.5f ? a : b;
            float s = Mathf.Sin(theta);
            return (Mathf.Sin((1f - t) * theta) / s) * a + (Mathf.Sin(t * theta) / s) * b;
        }

        /// <summary>
        /// 원본 노멀과 방사형 노멀을 mix 로 섞습니다.
        /// weights 가 있으면 정점별로 mix 에 곱해집니다(머리만 적용하는 용도).
        /// </summary>
        public static Vector3[] Apply(Vector3[] verts, Vector3[] normals,
                                      Vector3 center, float mix, float[] weights = null)
        {
            if (verts.Length != normals.Length)
                throw new ArgumentException("정점과 노멀 개수가 다릅니다: " + verts.Length + " / " + normals.Length);

            var outN = new Vector3[normals.Length];
            for (int i = 0; i < normals.Length; i++)
            {
                float m = weights != null ? mix * weights[i] : mix;

                Vector3 radial = verts[i] - center;
                float len = radial.magnitude;
                // 중심과 겹치는 정점은 방향이 정의되지 않으므로 원본을 그대로 둡니다.
                if (len < 1e-9f || m <= 0f) { outN[i] = normals[i].normalized; continue; }
                radial /= len;

                outN[i] = SlerpSafe(normals[i].normalized, radial, m).normalized;
            }
            return outN;
        }

        /// <summary>두 노멀 배열의 각도 차이 통계. 검증에 씁니다.</summary>
        public static void Compare(Vector3[] a, Vector3[] b,
                                   out float mean, out float median, out float max)
        {
            int n = Mathf.Min(a.Length, b.Length);
            var ang = new float[n];
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                float d = Mathf.Clamp(Vector3.Dot(a[i].normalized, b[i].normalized), -1f, 1f);
                ang[i] = Mathf.Acos(d) * Mathf.Rad2Deg;
                sum += ang[i];
            }
            Array.Sort(ang);
            mean = (float)(sum / n);
            median = ang[n / 2];
            max = ang[n - 1];
        }
    }
}