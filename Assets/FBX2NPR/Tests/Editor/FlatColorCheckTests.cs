using FBX2NPR.EditorTools;
using NUnit.Framework;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 텍스처 평탄화 직전의 색 검사(FBX2NPRConverter.FlatColorBlocked)를 실제 변환 기록의 값으로 지킵니다 (평탄색, 대상 영역의 원래 평균색).
    /// 평탄화가 돌던 텍스처(Ch07, The Boss, Emma)는 통과하고, 색도가 다른 Jones 와
    /// 머리카락까지 피부색으로 칠하던 MeshyWoman(색도로는 통과, 밝기 0.22배)은 막아야 합니다.
    /// </summary>
    public class FlatColorCheckTests
    {
        [Test]
        public void FlatteningStaysOffUnlessAsked()
        {
            // 텍스처를 고치는 단계라 기본은 끔. 그림풍 텍스처(Tanya, Emma)에서 켜면 그려 둔 눈이 지워졌다.
            Assert.That(new FBX2NPRAssetSettings().flattenFaceSkin, Is.False);
        }

        [TestCase("Ch07", 189, 143, 121, 170f, 125f, 98f, false)]
        [TestCase("TheBoss", 200, 122, 90, 175f, 109f, 82f, false)]
        [TestCase("Emma", 206, 151, 132, 192f, 134f, 113f, false)]
        [TestCase("Jones", 145, 138, 118, 84f, 93f, 84f, true)]
        [TestCase("MeshyWoman", 253, 224, 206, 53f, 50f, 48f, true)]
        public void BlocksOnlyOtherColours(string model, int fr, int fg, int fb, float tr, float tg, float tb, bool expectBlocked)
        {
            float gap, luma;
            bool blocked = FBX2NPRConverter.FlatColorBlocked(fr, fg, fb, tr, tg, tb, out gap, out luma);
            Assert.That(blocked, Is.EqualTo(expectBlocked), string.Format("{0}: 색 차이 {1:F3}, 밝기 비 {2:F2}", model, gap, luma));
        }
    }
}
