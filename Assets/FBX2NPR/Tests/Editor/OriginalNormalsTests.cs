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
    }
}
