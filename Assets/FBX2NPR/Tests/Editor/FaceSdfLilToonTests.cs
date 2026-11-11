using NUnit.Framework;
using UnityEngine;
using FBX2NPR.EditorTools;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 얼굴 SDF 맵을 lilToon 의 SDF 그림자 마스크 형식으로 바꾸는 계산(FaceSdfLilToon)을 지킵니다. 파일 없이 수만 봅니다.
    /// 기대값은 lilToon 2.4.1 소스(lil_common_frag.hlsl, SDF 분기)의 식으로 손으로 계산한 값입니다:
    ///   v = saturate(0.5·cosθ + 0.5·s + 0.25), v 가 _ShadowBorder(b) 보다 크면 밝음. R = 물체 −X 쪽 빛, G = +X 쪽 빛, B = 보통 그림자와 섞기, A = 그늘 세기.
    /// </summary>
    public class FaceSdfLilToonTests
    {
        // lilToon 이 칸을 밝게 그리는지 (테스트 쪽의 독립 기준: lilToon 소스의 식 그대로, 경계 폭 0)
        static bool LilLit(float thetaDeg, float s, float b)
        {
            float v = Mathf.Clamp01(0.5f * Mathf.Cos(thetaDeg * Mathf.Deg2Rad) + 0.5f * s + 0.25f);
            return v > b;
        }

        static Color32[] Fill(int n, Color32 c) { var a = new Color32[n]; for (int i = 0; i < n; i++) a[i] = c; return a; }
        static float[] Ones(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = 1f; return a; }

        [Test]
        public void AngleOfDecodesOurMap()
        {
            Assert.That(FaceSdfLilToon.AngleOf(0), Is.EqualTo(0f).Within(1e-4f), "0 = 처음부터 그늘");
            Assert.That(FaceSdfLilToon.AngleOf(255), Is.EqualTo(180f).Within(1e-4f), "255 = 끝까지 밝음");
            Assert.That(FaceSdfLilToon.AngleOf(128), Is.EqualTo(90.36f).Within(0.05f), "(128/255 − 2/255) / (1 − 4/255) × 180");
        }

        [Test]
        public void LilValueMatchesHandComputedValues()
        {
            Assert.That(FaceSdfLilToon.LilValue(90f, 0.5f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(FaceSdfLilToon.LilValue(60f, 0.5f), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(FaceSdfLilToon.LilValue(120f, 0.5f), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(FaceSdfLilToon.LilValue(30f, 0.5f), Is.EqualTo(0f), "범위보다 이른 각도는 0 으로 잘린다");
            Assert.That(FaceSdfLilToon.LilValue(90f, 0.75f), Is.EqualTo(1f).Within(1e-5f));
        }

        // 바꾼 값을 lilToon 식에 넣으면, 범위 안에서는 우리 맵과 같은 각도에서 어두워진다
        [Test]
        public void LilToonDarkensAtOurAngleInsideWindow()
        {
            float s = FaceSdfLilToon.LilValue(90f, FaceSdfLilToon.Border);
            Assert.That(LilLit(80f, s, FaceSdfLilToon.Border), Is.True, "80° 에서는 아직 밝다");
            Assert.That(LilLit(100f, s, FaceSdfLilToon.Border), Is.False, "100° 에서는 어둡다");
            float s2 = FaceSdfLilToon.LilValue(70f, FaceSdfLilToon.Border);
            Assert.That(LilLit(65f, s2, FaceSdfLilToon.Border), Is.True);
            Assert.That(LilLit(75f, s2, FaceSdfLilToon.Border), Is.False);
        }

        // 내보낼 때 쓰는 경계는 lilToon 기본값 0.5: 빛 60°~120° 를 담는다
        [Test]
        public void WindowFollowsBorder()
        {
            float lo, hi;
            FaceSdfLilToon.Window(FaceSdfLilToon.Border, out lo, out hi);
            Assert.That(FaceSdfLilToon.Border, Is.EqualTo(0.5f));
            Assert.That(lo, Is.EqualTo(60f).Within(0.01f)); Assert.That(hi, Is.EqualTo(120f).Within(0.01f));
            FaceSdfLilToon.Window(0.75f, out lo, out hi);
            Assert.That(lo, Is.EqualTo(0f).Within(0.01f)); Assert.That(hi, Is.EqualTo(90f).Within(0.01f));
            FaceSdfLilToon.Window(0.25f, out lo, out hi);
            Assert.That(lo, Is.EqualTo(90f).Within(0.01f)); Assert.That(hi, Is.EqualTo(180f).Within(0.01f));
        }

        // lilToon 마스크에는 코 채널이 없다. 코 그림자는 큰 명암선에 합친다: 칸마다 먼저 어두워지는 쪽 (값이 작은 쪽)
        [Test]
        public void MergeNoseTakesEarlierShadow()
        {
            var m = FaceSdfLilToon.MergeNose(new[] { new Color32(200, 100, 50, 255) });
            Assert.That(m[0].r, Is.EqualTo(50), "오른쪽 빛: 코 그림자가 먼저 생긴다");
            Assert.That(m[0].g, Is.EqualTo(100), "왼쪽 빛: 코 그림자가 없으면(255) 그대로");
        }

        // 얼굴 사각형(UV 0.25~0.75) 4×4 맵 → 전체 UV 8×8. 우리 R(오른쪽 빛) = 255(끝까지 밝음), G(왼쪽 빛) = 0(처음부터 그늘).
        // lilToon R 은 물체 −X 쪽(캐릭터 왼쪽) 빛이라 우리 G 가, lilToon G 에는 우리 R 이 들어간다.
        [Test]
        public void ConvertSwapsSidesAndExpandsFaceRect()
        {
            int ow, oh;
            var rect = new Rect(0.25f, 0.25f, 0.5f, 0.5f);
            FaceSdfLilToon.OutputSize(4, 4, rect, 4096, out ow, out oh);
            Assert.That(ow, Is.EqualTo(8)); Assert.That(oh, Is.EqualTo(8));
            var lil = FaceSdfLilToon.Convert(Fill(16, new Color32(255, 0, 0, 0)), 4, 4, rect, Ones(64), ow, oh);
            var inside = lil[3 * 8 + 3];
            Assert.That(inside.r, Is.EqualTo(0), "왼쪽 빛(우리 G = 0°) → s = 0");
            Assert.That(inside.g, Is.EqualTo(255), "오른쪽 빛(우리 R = 180°) → s = 1");
            Assert.That(inside.b, Is.EqualTo(0), "얼굴 가중치 1 → SDF 만 쓴다");
            Assert.That(inside.a, Is.EqualTo(255), "한쪽 빛에서만 끝까지 밝으면 그늘 세기는 그대로");
        }

        // 각도를 먼저 쌍선형으로 읽고 나서 s 로 바꾼다. 입력 2칸이 0° 와 120° 일 때,
        // 출력 x = 1 은 30° 라 s = 0 (값을 먼저 바꿔 섞으면 0.25), x = 2 는 90° 라 s = 0.5 (먼저 섞으면 0.75) 다.
        [Test]
        public void ConvertInterpolatesAnglesBeforeRemapping()
        {
            byte a0 = 0;                                                        // 0°
            byte a120 = (byte)Mathf.RoundToInt(FaceSdfCombine.Encode(120f) * 255f);
            var ours = new[] { new Color32(a0, a0, 0, 0), new Color32(a120, a120, 0, 0) };   // 2×1, 사각형이 UV 전체
            int ow, oh;
            var rect = new Rect(0f, 0f, 1f, 1f);
            FaceSdfLilToon.OutputSize(2, 1, rect, 4096, out ow, out oh);
            Assert.That(ow, Is.EqualTo(4)); Assert.That(oh, Is.EqualTo(4));
            var lil = FaceSdfLilToon.Convert(ours, 2, 1, rect, Ones(16), ow, oh);
            Assert.That(lil[1].r, Is.EqualTo(0), "30° → 범위(60°~120°)보다 이르니 s = 0");
            Assert.That(lil[2].r, Is.EqualTo(128).Within(2), "90° 근처 → s ≈ 0.5");
        }

        // B 는 1 − 얼굴 가중치: 우리 셰이더는 _FaceWeightOn 재질에서 얼굴 명암을 가중치만큼만 쓰고 나머지(몸)는 보통 명암이다.
        // A(그늘 세기)는 양쪽 빛 모두 끝까지 밝은 칸(눈 등)만 1 − 얼굴 가중치로 낮춘다. 한 범위로는 "늘 밝음"을 담을 수 없다.
        [Test]
        public void ConvertFollowsFaceWeightAndKeepsAlwaysLitTexels()
        {
            var w = new float[16]; w[5] = 1f; w[6] = 0.5f;   // 나머지 0
            var lil = FaceSdfLilToon.Convert(Fill(16, new Color32(255, 255, 0, 0)), 4, 4, new Rect(0f, 0f, 1f, 1f), w, 4, 4);
            Assert.That(lil[5].b, Is.EqualTo(0)); Assert.That(lil[5].a, Is.EqualTo(0), "늘 밝은 얼굴 칸은 그늘 세기 0");
            Assert.That(lil[6].b, Is.EqualTo(128).Within(1)); Assert.That(lil[6].a, Is.EqualTo(128).Within(1));
            Assert.That(lil[0].b, Is.EqualTo(255), "얼굴 가중치 0 → 보통 그림자"); Assert.That(lil[0].a, Is.EqualTo(255), "몸의 그늘 세기는 그대로");
        }

        // 얼굴이 텍스처의 작은 부분이면 전체 UV 로 펼친 크기가 커진다. 긴 변이 maxSize 를 넘으면 같은 비율로 줄인다 (4의 배수 유지).
        [Test]
        public void OutputSizeCapsLongSide()
        {
            int ow, oh;
            FaceSdfLilToon.OutputSize(4, 4, new Rect(0f, 0f, 0.25f, 0.125f), 16, out ow, out oh);
            Assert.That(oh, Is.EqualTo(16), "세로 32 → 16");
            Assert.That(ow, Is.EqualTo(8), "가로 16 → 같은 비율로 8");
        }

        // 얼굴 칸 삼각형을 UV 에 그려 칸마다 얼굴 가중치를 담는다. 삼각형 밖은 0. 가장자리는 한 칸 넓힌다:
        // lilToon 이 쌍선형으로 읽을 때 얼굴 칸에 보통 그림자가 섞여 이음매가 생기지 않게.
        [Test]
        public void FaceWeightMapRastersTrianglesWithMargin()
        {
            var uv = new[] { new Vector2(0f, 0f), new Vector2(0.9f, 0f), new Vector2(0f, 0.9f) };   // 8×8 에서 i + j ≤ 6 칸의 중심이 안쪽
            var m = FaceSdfLilToon.FaceWeightMap(uv, new[] { 1f, 1f, 1f }, new[] { 0, 1, 2 }, 8, 8);
            Assert.That(m[0], Is.EqualTo(1f), "안쪽");
            Assert.That(m[3 * 8 + 4], Is.EqualTo(1f), "i + j = 7: 바로 옆이라 넓힌다");
            Assert.That(m[7 * 8 + 7], Is.EqualTo(0f), "먼 칸");
            var half = FaceSdfLilToon.FaceWeightMap(uv, new[] { 0.5f, 0.5f, 0.5f }, new[] { 0, 1, 2 }, 8, 8);
            Assert.That(half[0], Is.EqualTo(0.5f).Within(1e-5f), "꼭짓점 가중치를 잇는다");
        }
    }
}
