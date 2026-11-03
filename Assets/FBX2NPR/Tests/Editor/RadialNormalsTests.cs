using System;
using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 얼굴 노멀 정리(RadialNormals)의 수식이 바뀌지 않았는지 지킵니다. 파일 없이 수식만 봅니다.
    /// 블렌더 정답지와의 대조는 BlenderOracleTests 가 합니다.
    /// </summary>
    public class RadialNormalsTests
    {
        [Test]
        public void MixZeroKeepsOriginalNormal()
        {
            // 길이가 1이 아닌 노멀도 정규화돼서 나와야 합니다.
            var outN = RadialNormals.Apply(new[] { new Vector3(0.3f, 0.1f, 0.2f) }, new[] { new Vector3(0f, 0f, 2f) }, Vector3.zero, 0f);
            Assert.That(Vector3.Angle(outN[0], Vector3.forward), Is.LessThan(1e-3f));
            Assert.That(outN[0].magnitude, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void MixOneIsRadialDirection()
        {
            var center = new Vector3(0f, 1.6f, 0f);
            var v = new Vector3(0.05f, 1.7f, 0.08f);
            var outN = RadialNormals.Apply(new[] { v }, new[] { Vector3.up }, center, 1f);
            Assert.That(Vector3.Angle(outN[0], v - center), Is.LessThan(1e-3f));
        }

        // Lerp 후 정규화로 구현했을 때 블렌더와 최대 34도 어긋났습니다.
        // 구면 보간이면 사잇각이 커도 섞은 비율만큼 정확히 돌아갑니다. 161도는 실제 머리 메시에서 나온 가장 큰 사잇각입니다.
        [TestCase(90f, 0.8f)]
        [TestCase(150f, 0.25f)]
        [TestCase(161f, 0.4f)]
        public void PartialMixRotatesByMixFraction(float betweenDeg, float mix)
        {
            Vector3 radial = Quaternion.AngleAxis(betweenDeg, Vector3.up) * Vector3.forward;
            var outN = RadialNormals.Apply(new[] { radial }, new[] { Vector3.forward }, Vector3.zero, mix);
            Assert.That(Vector3.Angle(Vector3.forward, outN[0]), Is.EqualTo(betweenDeg * mix).Within(0.01f));
            Assert.That(outN[0].magnitude, Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void WeightsScaleMixPerVertex()
        {
            Vector3 radial = Quaternion.AngleAxis(120f, Vector3.up) * Vector3.forward;
            var outN = RadialNormals.Apply(new[] { radial, radial }, new[] { Vector3.forward, Vector3.forward }, Vector3.zero, 1f, new[] { 0f, 0.5f });
            Assert.That(Vector3.Angle(outN[0], Vector3.forward), Is.LessThan(1e-3f), "가중치 0 (머리 밖) 은 원본 그대로");
            Assert.That(Vector3.Angle(outN[1], Vector3.forward), Is.EqualTo(60f).Within(0.01f));
        }

        // 노멀·UV 가 갈라지는 자리에서 쪼개진 정점을 여러 번 세면 중심이 밀려, mix=1 에서도 블렌더와 1.1도 어긋났습니다.
        [Test]
        public void CentroidCountsSplitVerticesOnce()
        {
            var a = new Vector3(1f, 0f, 0f); var b = new Vector3(0f, 1f, 0f); var c = new Vector3(0f, 0f, 1f);
            Vector3 got = RadialNormals.Centroid(new[] { a, b, c, c, c, c });
            Assert.That((got - (a + b + c) / 3f).magnitude, Is.LessThan(1e-6f));
        }

        [Test]
        public void ApplyRejectsMismatchedArrays()
        {
            Assert.Throws<ArgumentException>(() => RadialNormals.Apply(new Vector3[2], new Vector3[1], Vector3.zero, 1f));
        }
    }
}
