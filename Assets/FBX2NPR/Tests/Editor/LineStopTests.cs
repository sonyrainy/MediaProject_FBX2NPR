using FBX2NPR.EditorTools;
using NUnit.Framework;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 명암선 위치 자동 보정(FaceLandmarks.LineStopFor)의 규칙을 지킵니다.
    /// 기준 빛에서 눈 한가운데가 밝은 모델(지금까지의 실사형 모두)은 기본값 -0.20 을 그대로 받아야 기존 결과가 한 화소도 안 바뀌고,
    /// 눈 한가운데가 그늘인 모델(Emma)만 눈꼬리 바깥이 밝아지는 값으로 내려가되 -0.55 아래로는 가지 않아야 합니다.
    /// </summary>
    public class LineStopTests
    {
        const float Lift = 0.015f, Soft = 0.015f;   // 눈 높이의 턱 보정, 명암선 경계 폭 (변환 도구 기본)

        [Test]
        public void KeepsDefaultWhenEyeCentreIsLit()
        {
            bool moved;
            float s = FaceLandmarks.LineStopFor(-0.03f, Lift, -0.29f, Lift, Soft, out moved);   // Ch07 처럼 눈 한가운데가 밝음
            Assert.That(moved, Is.False);
            Assert.That(s, Is.EqualTo(-0.20f));   // 계산값이 아니라 기본값 그대로여야 한다
        }

        [Test]
        public void MovesLineOutsideTheEyeCornerWhenEyeCentreIsShaded()
        {
            bool moved;
            float s = FaceLandmarks.LineStopFor(-0.45f, Lift, -0.49f, Lift, Soft, out moved);   // Emma 처럼 눈 한가운데가 그늘
            Assert.That(moved, Is.True);
            Assert.That(s, Is.EqualTo(-0.49f - Lift - Soft).Within(1e-6f));   // 눈꼬리 바깥이 경계 폭까지 밝아지는 값
        }

        [Test]
        public void CheekKeepsDefaultWhenItIsAlreadyLit()
        {
            Assert.That(FaceLandmarks.LineStopForWidth(-0.10f, Soft), Is.EqualTo(FaceLandmarks.LineStopDefault));
        }

        [Test]
        public void CheekPullsTheLineOutWhenItIsShaded()
        {
            Assert.That(FaceLandmarks.LineStopForWidth(-0.40f, Soft), Is.EqualTo(-0.40f - Soft).Within(1e-6f));
            Assert.That(FaceLandmarks.LineStopForWidth(-0.80f, Soft), Is.EqualTo(FaceLandmarks.LineStopMin));
        }

        [Test]
        public void NeverGoesBelowTheFloor()
        {
            bool moved;
            float s = FaceLandmarks.LineStopFor(-0.60f, Lift, -0.75f, Lift, Soft, out moved);
            Assert.That(moved, Is.True);
            Assert.That(s, Is.EqualTo(FaceLandmarks.LineStopMin));
        }
    }
}
