using System.Collections.Generic;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 각도별 마스크를 SDF 맵으로 합치는 계산(FaceSdfCombine)을 지킵니다.
    /// 마스크 사이 각도는 경계까지의 거리로 나눠 잇고, UV 조각 밖의 칸이 그늘 경계로 잡히면 안 됩니다.
    /// </summary>
    public class FaceSdfCombineTests
    {
        const int W = 64, H = 8;

        // x < edge 인 칸이 밝은 마스크
        static bool[] Edge(int edge)
        {
            var m = new bool[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) m[y * W + x] = x < edge;
            return m;
        }

        static bool[] All(bool v) { var m = new bool[W * H]; for (int i = 0; i < m.Length; i++) m[i] = v; return m; }

        static float At(float[] t, int x) { return t[H / 2 * W + x]; }

        [Test]
        public void SignedDistanceIsPlusMinusHalfAtTheEdge()
        {
            var d = FaceSdfCombine.SignedDistance(Edge(20), W, H);
            Assert.That(At(d, 19), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(At(d, 20), Is.EqualTo(-0.5f).Within(1e-4f));
            Assert.That(At(d, 10), Is.EqualTo(9.5f).Within(1e-4f));
            Assert.That(At(d, 30), Is.EqualTo(-10.5f).Within(1e-4f));
        }

        [Test]
        public void AngleBetweenMasksFollowsTheDistanceToTheEdges()
        {
            // 0도에서 경계 x=40, 10도에서 30, 20도에서 20: 경계가 고르게 움직이면 값도 고르게 변해야 한다
            var masks = new List<bool[]> { Edge(40), Edge(30), Edge(20) };
            var t = FaceSdfCombine.Thresholds(masks, new List<float> { 0f, 10f, 20f }, All(true), W, H);
            Assert.That(FaceSdfCombine.Decode(At(t, 35)), Is.EqualTo(4.6f).Within(0.3f), "0도와 10도 사이");
            Assert.That(FaceSdfCombine.Decode(At(t, 25)), Is.EqualTo(14.6f).Within(0.3f), "10도와 20도 사이");
        }

        [Test]
        public void AlwaysLitAndAlwaysDarkUseTheEnds()
        {
            var masks = new List<bool[]> { Edge(40), Edge(30) };
            var t = FaceSdfCombine.Thresholds(masks, new List<float> { 0f, 10f }, All(true), W, H);
            Assert.That(At(t, 5), Is.EqualTo(1f), "끝까지 밝은 칸");
            Assert.That(At(t, 50), Is.EqualTo(0f), "처음부터 어두운 칸");
        }

        [Test]
        public void MarginKeepsAnglesInsideTheEnds()
        {
            Assert.That(FaceSdfCombine.Encode(0f), Is.GreaterThan(0f));
            Assert.That(FaceSdfCombine.Encode(180f), Is.LessThan(1f));
            Assert.That(FaceSdfCombine.Decode(FaceSdfCombine.Encode(77f)), Is.EqualTo(77f).Within(1e-3f));
        }

        [Test]
        public void TexelsOutsideTheFaceDoNotActAsShadow()
        {
            // 얼굴은 x < 30 까지. 밖의 칸을 그늘로 치면 얼굴 가장자리가 경계가 되어, 끝까지 밝은 칸의 값이 깎인다
            var covered = Edge(30);
            var lit = new List<bool[]> { Edge(30), Edge(30) };
            var t = FaceSdfCombine.Thresholds(lit, new List<float> { 0f, 10f }, covered, W, H);
            Assert.That(At(t, 29), Is.EqualTo(1f), "얼굴 가장자리 칸");
            Assert.That(At(t, 45), Is.EqualTo(1f), "얼굴 밖 칸은 가까운 얼굴 칸의 값을 받는다");
        }

        [Test]
        public void FirstDarkeningWinsWhenATexelLightsUpAgain()
        {
            // 0도 밝음, 10도 어두움, 20도 다시 밝음: 한 번만 어두워진다고 본다
            var masks = new List<bool[]> { All(true), All(false), All(true) };
            var t = FaceSdfCombine.Thresholds(masks, new List<float> { 0f, 10f, 20f }, All(true), W, H);
            Assert.That(FaceSdfCombine.Decode(At(t, 10)), Is.LessThanOrEqualTo(10f));
        }

        [Test]
        public void NearestCoveredReachesAcrossGaps()
        {
            var cov = new bool[W * H]; cov[0] = true; cov[W * H - 1] = true;
            var near = FaceSdfCombine.NearestCovered(cov, W, H);
            Assert.That(near[1], Is.EqualTo(0));
            Assert.That(near[W * H - 2], Is.EqualTo(W * H - 1));
        }
    }
}
