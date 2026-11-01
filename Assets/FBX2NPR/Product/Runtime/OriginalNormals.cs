using UnityEngine;

namespace FBX2NPR
{
    /// <summary>
    /// 원본 노멀(정리하기 전 노멀)을 정점의 노멀·탄젠트 기준 방향(탄젠트 공간)으로 바꿔 UV4 에 싣고, 다시 꺼냅니다.
    ///
    /// 예전에는 스킨 메시의 원본 노멀을 탄젠트 자리에 실었습니다. UV3 에 실은 물체 공간 노멀은 뼈를 따라 돌지 않기 때문입니다.
    /// 대신 탄젠트를 잃어서 노멀맵을 못 썼고, 다른 셰이더에서는 탄젠트가 엉뚱한 값이었습니다.
    /// 탄젠트 공간으로 바꿔 두면 스키닝이 노멀·탄젠트를 돌릴 때 원본 노멀도 같은 축을 따라 돌고, 탄젠트는 진짜 탄젠트로 남습니다.
    /// 셰이더 쪽 복원은 OriginalNormals.hlsl 의 DecodeOriginalNormal 이 같은 축으로 합니다.
    /// </summary>
    public static class OriginalNormals
    {
        /// <summary>탄젠트 공간 원본 노멀을 싣는 UV 채널 (TEXCOORD4). UV3 에는 예전처럼 물체 공간 원본 노멀이 남습니다 (정적 메시·분석 도구용).</summary>
        public const int Channel = 4;

        /// <summary>외곽선 피처가 원본 노멀로 다시 그릴 렌더러에 켜는 렌더링 레이어 (8번째 비트). 변환 도구가 얼굴 렌더러에 켭니다.</summary>
        public const uint LineLayer = 1u << 7;

        // 노멀에 직교화한 탄젠트와 바이탄젠트. 탄젠트가 없거나 노멀과 평행하면 셰이더와 같은 규칙으로 다른 축을 고릅니다.
        static void Frame(Vector3 n, Vector4 t, out Vector3 tt, out Vector3 b)
        {
            tt = (Vector3)t - n * Vector3.Dot(n, t);
            if (tt.sqrMagnitude < 1e-12f)
            {
                Vector3 a = Mathf.Abs(n.x) < 0.9f ? Vector3.right : Vector3.up;
                tt = a - n * Vector3.Dot(n, a);
            }
            tt.Normalize();
            b = Vector3.Cross(n, tt) * (t.w < 0f ? -1f : 1f);
        }

        public static Vector3 Encode(Vector3 orig, Vector3 normal, Vector4 tangent)
        {
            Vector3 n = normal.normalized, tt, b;
            Frame(n, tangent, out tt, out b);
            Vector3 o = orig.normalized;
            return new Vector3(Vector3.Dot(o, tt), Vector3.Dot(o, b), Vector3.Dot(o, n));
        }

        public static Vector3 Decode(Vector3 ts, Vector3 normal, Vector4 tangent)
        {
            Vector3 n = normal.normalized, tt, b;
            Frame(n, tangent, out tt, out b);
            return (ts.x * tt + ts.y * b + ts.z * n).normalized;
        }

        /// <summary>메시의 지금 노멀·탄젠트를 기준으로 원본 노멀을 UV4 에 싣습니다. 노멀을 다 정리한 뒤에 불러야 합니다. 탄젠트가 없으면 지금 노멀로 계산합니다.
        /// 노멀에 직교하지 않는 탄젠트는 되돌릴 때와 같은 축(Frame)으로 고쳐 다시 넣습니다. UV 가 한 점에 뭉친 곳(머리핀 같은 작은 장신구, 머리카락 일부)에서는
        /// 임포터가 탄젠트를 구하지 못해 기본값 (1,0,0)을 남기는데, 그대로 두면 노멀맵과 다른 셰이더의 탄젠트 축이 비뚤어집니다. UV4 값은 고치기 전과 같습니다.</summary>
        public static void Write(Mesh mesh, Vector3[] orig)
        {
            var n = mesh.normals; var t = mesh.tangents;
            if (t == null || t.Length != n.Length) { mesh.RecalculateTangents(); t = mesh.tangents; }
            if (t == null || t.Length != n.Length) t = new Vector4[n.Length];   // 탄젠트를 못 구한 메시: 아래에서 전부 고친다
            var ts = new Vector3[n.Length];
            bool repaired = false;
            for (int i = 0; i < n.Length; i++)
            {
                if (NeedsRepair(n[i], t[i])) { t[i] = Repair(n[i], t[i]); repaired = true; }
                ts[i] = Encode(orig[i], n[i], t[i]);
            }
            if (repaired) mesh.tangents = t;
            mesh.SetUVs(Channel, ts);
        }

        // 노멀에 직교하는 단위 탄젠트이고 w 가 ±1 이면 그대로 둔다 (임포터와 RecalculateTangents 가 제대로 구한 탄젠트는 건드리지 않는다)
        static bool NeedsRepair(Vector3 normal, Vector4 t)
        {
            Vector3 tv = t; float len = tv.magnitude;
            if (Mathf.Abs(len - 1f) > 1e-3f || Mathf.Abs(Mathf.Abs(t.w) - 1f) > 1e-3f) return true;
            return Mathf.Abs(Vector3.Dot(normal.normalized, tv / len)) > 1e-3f;
        }

        static Vector4 Repair(Vector3 normal, Vector4 t)
        {
            Vector3 tt, b;
            Frame(normal.normalized, t, out tt, out b);
            return new Vector4(tt.x, tt.y, tt.z, t.w < 0f ? -1f : 1f);
        }
    }
}
