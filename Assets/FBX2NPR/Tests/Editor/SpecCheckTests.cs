using System.IO;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 변환 전 규격 검사(FBX2NPRConverter.CheckSpec)의 계산을 지킵니다.
    /// - 얼굴 방향은 노멀 값이 아니라 삼각형 위치로 구합니다. 앞면이 -Z 인 판과 돌린 판을 바르게 읽고, 한쪽만 보는 판의 확신도는 1 이어야 합니다.
    /// - 얼굴 밖 정점이 낀 삼각형은 세지 않아야 합니다.
    /// - 바이너리 FBX 에서 표정 클래스 이름(BlendShapeChannel)을 찾아야 합니다.
    /// </summary>
    public class SpecCheckTests
    {
        // 정점 4개 판. 유니티 감김으로 -Z 쪽이 앞면 (BlendShapeFaceTests.Quad 와 같은 배치)
        static readonly Vector3[] V = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
        static readonly int[] T = { 0, 2, 1, 1, 2, 3 };

        [Test]
        public void FacingComesFromTrianglePositions()
        {
            var w = new[] { 1f, 1f, 1f, 1f };
            Vector3 s = Vector3.zero; double a = 0; float conf;
            FBX2NPRConverter.AddFaceArea(V, T, w, Matrix4x4.identity, ref s, ref a);
            Assert.That(Mathf.Abs(FBX2NPRConverter.FacingYaw(s, a, out conf)), Is.EqualTo(180f).Within(0.01f), "앞면이 -Z 인 판");
            Assert.That(conf, Is.EqualTo(1f).Within(1e-4f));

            s = Vector3.zero; a = 0;
            FBX2NPRConverter.AddFaceArea(V, T, w, Matrix4x4.Rotate(Quaternion.Euler(0f, 90f, 0f)), ref s, ref a);
            Assert.That(FBX2NPRConverter.FacingYaw(s, a, out conf), Is.EqualTo(-90f).Within(0.01f), "Y 축으로 90도 돌리면 -X 를 본다");
        }

        [Test]
        public void TrianglesOutsideTheFaceAreIgnored()
        {
            Vector3 s = Vector3.zero; double a = 0;
            FBX2NPRConverter.AddFaceArea(V, T, new[] { 1f, 0f, 1f, 1f }, Matrix4x4.identity, ref s, ref a);   // 두 삼각형 모두 얼굴 밖 정점 1 을 쓴다
            Assert.That(a, Is.EqualTo(0d));
        }

        [Test]
        public void FileContainsFindsAsciiInBinary()
        {
            string p = Path.Combine(Path.GetTempPath(), "fbx2npr_spec_" + System.Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var bytes = new byte[64]; for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7);
                var k = System.Text.Encoding.ASCII.GetBytes("BlendShapeChannel"); System.Array.Copy(k, 0, bytes, 30, k.Length);
                File.WriteAllBytes(p, bytes);
                Assert.That(FBX2NPRConverter.FileContains(p, "BlendShapeChannel"), Is.True);
                Assert.That(FBX2NPRConverter.FileContains(p, "Deformer"), Is.False);
            }
            finally { if (File.Exists(p)) File.Delete(p); }
        }
    }
}
