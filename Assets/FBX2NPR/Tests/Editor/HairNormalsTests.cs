using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 머리카락 노멀 정리(HairNormals)의 캡슐 방향과 섞기를 지킵니다. 파일 없이 수식만 봅니다.
    /// 캡슐: 위쪽 구의 중심 center(머리 중심). 그 아래는 끝까지 세로 원통이라 노멀이 수평입니다.
    /// </summary>
    public class HairNormalsTests
    {
        static readonly Vector3 Center = new Vector3(0f, 1.5f, 0f);

        [Test]
        public void AboveCenterPointsAwayFromHeadCenter()
        {
            var p = new Vector3(0.05f, 1.62f, -0.04f);
            Assert.That(Vector3.Angle(HairNormals.CapsuleDir(p, Center), p - Center), Is.LessThan(1e-3f));
        }

        // 어깨 아래로 내려오는 긴 머리: 아래를 향해 통째로 어두워지지 않게 수평 방향이어야 한다
        [Test]
        public void BelowCenterIsHorizontal()
        {
            var d = HairNormals.CapsuleDir(new Vector3(-0.12f, 1.3f, 0.03f), Center);
            Assert.That(d.y, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(Vector3.Angle(d, new Vector3(-0.12f, 0f, 0.03f)), Is.LessThan(1e-3f));
        }

        // 허벅지까지 내려오는 긴 꼬리(유니티짱 tail_bottom): 아무리 내려가도 수평이어야 한다. 아래 끝을 구로 막으면 꼬리 끝이 아래를 향해 어두워진다
        [Test]
        public void FarBelowStaysHorizontal()
        {
            var d = HairNormals.CapsuleDir(new Vector3(0.1f, 0.5f, -0.05f), Center);
            Assert.That(d.y, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(Vector3.Angle(d, new Vector3(0.1f, 0f, -0.05f)), Is.LessThan(1e-3f));
        }

        [Test]
        public void MixZeroAndMaskKeepOriginal()
        {
            var pos = new[] { new Vector3(0.1f, 1.3f, 0f), new Vector3(0f, 1.3f, 0.1f) };
            var nrm = new[] { new Vector3(0f, 0f, 2f), new Vector3(0f, 2f, 0f) };
            var zero = HairNormals.Apply(pos, nrm, Center, 0f);
            Assert.That(Vector3.Angle(zero[0], Vector3.forward), Is.LessThan(1e-3f));
            Assert.That(zero[0].magnitude, Is.EqualTo(1f).Within(1e-5f));
            var masked = HairNormals.Apply(pos, nrm, Center, 1f, new[] { false, true });
            Assert.That(Vector3.Angle(masked[0], Vector3.forward), Is.LessThan(1e-3f), "마스크 밖 정점은 그대로");
            Assert.That(Vector3.Angle(masked[1], Vector3.forward), Is.LessThan(1e-3f), "마스크 안 정점은 캡슐 방향(+Z)");
        }

        [Test]
        public void PartialMixRotatesByMixFraction()
        {
            // 원래 노멀 +Z, 캡슐 방향 +X (사잇각 90°) → mix 0.25 면 22.5°
            var outN = HairNormals.Apply(new[] { new Vector3(0.1f, 1.3f, 0f) }, new[] { Vector3.forward }, Center, 0.25f);
            Assert.That(Vector3.Angle(Vector3.forward, outN[0]), Is.EqualTo(22.5f).Within(0.01f));
        }

        // 재질 이름이 메시 이름(PSphere2)이고 텍스처 이름에만 hair 가 있는 모델(Night Dress)도 머리카락 칸으로 잡는다. 머리띠(재질 body)는 아니다
        [Test]
        public void HairSlotByMaterialOrTextureName()
        {
            var s = new FBX2NPRAssetSettings();
            var tex = new Texture2D(2, 2) { name = "hair_back_m_baseColor" };
            var body = new Texture2D(2, 2) { name = "body_01" };
            var sh = Shader.Find("Hidden/InternalErrorShader");
            var byName = new Material(sh) { name = "Hair_front2" };
            var byTex = new Material(sh) { name = "PSphere2" };
            var band = new Material(sh) { name = "body" };
            try
            {
                Assert.That(s.IsHairSlot(byName, null), Is.True);
                Assert.That(s.IsHairSlot(byTex, tex), Is.True);
                Assert.That(s.IsHairSlot(band, body), Is.False);
                Assert.That(s.IsHairSlot(null, null), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); UnityEngine.Object.DestroyImmediate(body); UnityEngine.Object.DestroyImmediate(byName); UnityEngine.Object.DestroyImmediate(byTex); UnityEngine.Object.DestroyImmediate(band); }
        }

        [Test]
        public void PointOnAxisKeepsOriginal()
        {
            var outN = HairNormals.Apply(new[] { new Vector3(0f, 1.3f, 0f) }, new[] { Vector3.right }, Center, 1f);
            Assert.That(Vector3.Angle(outN[0], Vector3.right), Is.LessThan(1e-3f));
        }
    }
}
