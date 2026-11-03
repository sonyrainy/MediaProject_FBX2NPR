using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 원본 노멀을 탄젠트 공간으로 싣고 되돌리는 식(OriginalNormals)을 지킵니다. 파일 없이 수식만 봅니다.
    /// 셰이더의 DecodeOriginalNormal 도 같은 축을 쓰므로, 여기서 맞으면 스키닝 뒤의 셰이더 복원도 같은 값을 냅니다.
    /// </summary>
    public class OriginalNormalsTests
    {
        static Vector3 Dir(float yaw, float pitch) { return Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward; }

        [TestCase(10f, 20f, 1f)]
        [TestCase(170f, -60f, 1f)]
        [TestCase(95f, 80f, -1f)]   // 바이탄젠트가 뒤집힌 정점 (탄젠트 w = -1, UV 를 좌우 뒤집어 붙인 곳)
        public void DecodeReturnsTheEncodedNormal(float yaw, float pitch, float w)
        {
            Vector3 n = Dir(30f, 10f);
            Vector4 t = new Vector4(Dir(120f, 0f).x, Dir(120f, 0f).y, Dir(120f, 0f).z, w);   // 노멀과 직교가 아니어도 된다
            Vector3 orig = Dir(yaw, pitch);
            Vector3 back = OriginalNormals.Decode(OriginalNormals.Encode(orig, n, t), n, t);
            Assert.That(Vector3.Angle(orig, back), Is.LessThan(1e-3f));
        }

        // 스키닝은 노멀과 탄젠트를 같은 회전으로 돌린다. 원본 노멀도 그 회전만큼 돌아서 나와야 고개를 돌려도 코 음영·선이 따라간다.
        [Test]
        public void DecodeFollowsRotationOfNormalAndTangent()
        {
            Vector3 n = Dir(-20f, 15f), orig = Dir(40f, -25f);
            Vector4 t = new Vector4(1f, 0.2f, 0.1f, 1f);
            Vector3 ts = OriginalNormals.Encode(orig, n, t);

            Quaternion bone = Quaternion.Euler(35f, -70f, 20f);
            Vector3 tr = bone * new Vector3(t.x, t.y, t.z);
            Vector3 back = OriginalNormals.Decode(ts, bone * n, new Vector4(tr.x, tr.y, tr.z, t.w));
            Assert.That(Vector3.Angle(bone * orig, back), Is.LessThan(1e-3f));
        }

        // 탄젠트가 없거나 노멀과 평행한 정점(UV 가 없거나 찌그러진 곳)도 같은 규칙으로 축을 골라 되돌릴 수 있어야 한다.
        [TestCase(0f, 0f, 0f)]
        [TestCase(0f, 1f, 0f)]
        public void DegenerateTangentStillRoundTrips(float tx, float ty, float tz)
        {
            Vector3 n = Vector3.up, orig = Dir(60f, -30f);
            Vector4 t = new Vector4(tx, ty, tz, 1f);
            Vector3 back = OriginalNormals.Decode(OriginalNormals.Encode(orig, n, t), n, t);
            Assert.That(Vector3.Angle(orig, back), Is.LessThan(1e-3f));
        }

        [Test]
        public void WriteFillsUV4AndKeepsRealTangents()
        {
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                normals = new[] { Vector3.back, Vector3.back, Vector3.back },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.up },
                triangles = new[] { 0, 1, 2 }
            };
            try
            {
                var orig = new[] { Dir(10f, 5f) * -1f, Dir(-15f, 20f) * -1f, Vector3.back };
                OriginalNormals.Write(mesh, orig);

                var ts = new System.Collections.Generic.List<Vector3>();
                mesh.GetUVs(OriginalNormals.Channel, ts);
                var n = mesh.normals; var t = mesh.tangents;
                Assert.That(ts.Count, Is.EqualTo(3));
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(Mathf.Abs(Vector3.Dot(n[i], t[i])), Is.LessThan(1e-3f), "탄젠트는 노멀에 직교하는 진짜 탄젠트여야 합니다.");
                    Assert.That(Vector3.Angle(orig[i], OriginalNormals.Decode(ts[i], n[i], t[i])), Is.LessThan(1e-3f));
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        // UV 가 한 점에 뭉친 곳(Felis 머리핀 등)은 임포터가 탄젠트를 못 구해 기본값 (1,0,0)을 남긴다. 노멀이 x 쪽이면 노멀과 거의 평행하다.
        // Write 는 이런 탄젠트(와 없는 탄젠트, w 가 0 인 탄젠트)를 되돌릴 때와 같은 축으로 고쳐 넣고, 제대로 된 탄젠트는 그대로 둔다.
        [Test]
        public void WriteRepairsTangentsThatAreNotOrthogonal()
        {
            Vector3 nx = new Vector3(1f, 0.2f, 0f).normalized;
            var good = new Vector4(1f, 0f, 0f, -1f);   // 노멀(뒤쪽)에 직교하는 정상 탄젠트, 바이탄젠트 뒤집힘
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one },
                normals = new[] { nx, nx, Vector3.up, Vector3.up, Vector3.back },
                uv = new Vector2[5],                    // UV 가 전부 한 점
                triangles = new[] { 0, 1, 2, 2, 3, 4 },
                tangents = new[]
                {
                    new Vector4(1f, 0f, 0f, 1f),        // 임포터 기본값: 노멀과 거의 평행
                    new Vector4(1f, 0f, 0f, 0f),        // w 도 0
                    new Vector4(0f, 1f, 0f, 1f),        // 노멀과 정확히 평행
                    Vector4.zero,                       // 탄젠트 없음
                    good
                }
            };
            try
            {
                var orig = new[] { Dir(80f, 10f), Dir(100f, -20f), Dir(20f, 70f), Dir(-30f, 60f), Dir(170f, 5f) };
                OriginalNormals.Write(mesh, orig);

                var ts = new System.Collections.Generic.List<Vector3>();
                mesh.GetUVs(OriginalNormals.Channel, ts);
                var n = mesh.normals; var t = mesh.tangents;
                for (int i = 0; i < n.Length; i++)
                {
                    Vector3 tv = t[i];
                    Assert.That(Mathf.Abs(Vector3.Dot(n[i], tv)), Is.LessThan(1e-4f), i + ": 노멀에 직교해야 합니다.");
                    Assert.That(tv.magnitude, Is.EqualTo(1f).Within(1e-4f), i + ": 단위 길이여야 합니다.");
                    Assert.That(Mathf.Abs(t[i].w), Is.EqualTo(1f), i + ": w 는 ±1 이어야 합니다.");
                    Assert.That(Vector3.Angle(orig[i], OriginalNormals.Decode(ts[i], n[i], t[i])), Is.LessThan(1e-3f), i + ": 고친 탄젠트로도 원본 노멀이 되돌아와야 합니다.");
                }
                Assert.That(t[4], Is.EqualTo(good), "제대로 된 탄젠트는 그대로 둡니다.");
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
