using System;
using UnityEngine;

namespace FBX2NPR
{
    /// <summary>
    /// 머리카락 노멀을 머리 중심 캡슐 바깥 방향과 섞습니다.
    ///
    /// 머리카락은 가닥(판)마다 노멀이 제각각이라, 툰 명암으로 끊어 칠하면 가닥마다 회색 조각이 생겼습니다.
    /// 얼굴처럼 한 점(구)으로 모으면 어깨 아래로 내려오는 긴 머리가 아래쪽을 향해 통째로 어두워지므로,
    /// 머리 중심 위쪽은 구, 그 아래는 끝까지 세로 원통으로 봅니다. 원통 구간의 노멀은 수평이라
    /// 허벅지까지 내려오는 꼬리도 빛의 좌우를 따라 한 덩어리로 명암을 받습니다.
    /// 처음에는 아래 끝도 구로 막았는데, 그 높이보다 내려간 꼬리 끝(유니티짱 tail_bottom)의 노멀이 평균 51° 아래를 향해서 아래 구를 뺐습니다 (2026-11-11).
    ///
    /// 섞는 방식은 얼굴과 같은 구면 선형 보간입니다 (RadialNormals.SlerpSafe).
    /// </summary>
    public static class HairNormals
    {
        /// <summary>
        /// 캡슐 바깥 방향. center 는 위쪽 구의 중심(머리 중심)입니다.
        /// center 보다 높으면 center 에서 바깥, 낮으면 높이와 상관없이 세로 축에서 수평으로 바깥입니다.
        /// </summary>
        public static Vector3 CapsuleDir(Vector3 p, Vector3 center)
        {
            float y = Mathf.Min(p.y, center.y);
            var d = p - new Vector3(center.x, y, center.z);
            float len = d.magnitude;
            return len > 1e-9f ? d / len : Vector3.zero;
        }

        /// <summary>
        /// 월드 위치·노멀을 받아 캡슐 방향과 mix 만큼 섞은 월드 노멀을 돌려줍니다.
        /// mask 가 있으면 true 인 정점만 바꿉니다 (머리카락 재질 칸의 정점). 축 위에 놓인 정점은 방향이 없어 그대로 둡니다.
        /// </summary>
        public static Vector3[] Apply(Vector3[] worldPos, Vector3[] worldNormals, Vector3 center, float mix, bool[] mask = null)
        {
            if (worldPos.Length != worldNormals.Length)
                throw new ArgumentException("정점과 노멀 개수가 다릅니다: " + worldPos.Length + " / " + worldNormals.Length);
            var outN = new Vector3[worldNormals.Length];
            for (int i = 0; i < outN.Length; i++)
            {
                var n = worldNormals[i].normalized;
                if (mix <= 0f || (mask != null && !mask[i])) { outN[i] = n; continue; }
                var c = CapsuleDir(worldPos[i], center);
                outN[i] = c == Vector3.zero ? n : RadialNormals.SlerpSafe(n, c, Mathf.Clamp01(mix)).normalized;
            }
            return outN;
        }
    }
}
