using System.Collections.Generic;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Utils;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 얼굴 기준점(FaceLandmarks)의 코끝 찾기와 코 그림자 도형을 지킵니다.
    /// 눈을 메시로 찾았으면 코끝은 눈높이 아래에서만 찾아야 합니다. Night Dress 는 이마 아래쪽이 코끝보다 앞으로 나와 이마를 코끝으로 잡았습니다.
    /// 눈 메시가 없으면 예전처럼 높이 조건 없이 가장 앞으로 나온 점을 잡습니다.
    /// 코에 형태가 있어도(임계값 0) 코 그림자는 원본 노멀 그림자 자리에 맞춘 도형으로 그려야 합니다.
    /// </summary>
    public class FaceLandmarksTests
    {
        // 이마(눈보다 위, 가장 앞)와 코끝(눈보다 아래, 4mm 뒤). 나머지 점은 얼굴 바운드를 만든다.
        static readonly Vector3 Brow = new Vector3(0f, 0.03f, 0.100f), Nose = new Vector3(0f, -0.03f, 0.096f);

        static FaceLandmarks.Src Face()
        {
            var w = new[] { new Vector3(-0.08f, -0.12f, -0.08f), new Vector3(0.08f, 0.12f, -0.08f), new Vector3(0.06f, 0f, 0.05f), new Vector3(-0.06f, 0f, 0.05f), Brow, Nose };
            var weights = new float[w.Length]; for (int i = 0; i < weights.Length; i++) weights[i] = 1f;
            return new FaceLandmarks.Src { name = "Head", world = w, weights = weights, isFace = true };
        }

        // 눈 메시: 높이 0 에 좌우 8개씩
        static FaceLandmarks.Src Eyes()
        {
            var w = new List<Vector3>();
            for (int i = 0; i < 8; i++) { float t = i / 7f; w.Add(new Vector3(0.02f + 0.02f * t, 0f, 0.06f)); w.Add(new Vector3(-0.02f - 0.02f * t, 0f, 0.06f)); }
            return new FaceLandmarks.Src { name = "Eye", world = w.ToArray(), weights = new float[w.Count], isFace = false };
        }

        [Test]
        public void NoseTipIsBelowTheEyesWhenTheBrowStandsOut()
        {
            var r = FaceLandmarks.Detect(new List<FaceLandmarks.Src> { Face(), Eyes() });
            Assert.That(r.eyesFound && !r.eyesEstimated, Is.True);
            Assert.That(r.noseFound, Is.True);
            Assert.That(r.noseTip, Is.EqualTo(Nose), "이마가 더 앞이어도 코끝은 눈 아래 점");
        }

        [Test]
        public void WithoutEyeMeshesTheMostForwardPointIsKept()
        {
            var r = FaceLandmarks.Detect(new List<FaceLandmarks.Src> { Face() });
            Assert.That(r.noseTip, Is.EqualTo(Brow), "눈 메시가 없으면 예전 규칙 그대로");
            Assert.That(r.eyesEstimated, Is.True);
        }

        // 코에 형태가 있는 얼굴: 0.5cm 간격 판(z 0.05), 코끝(0, -0.03)만 1cm 앞, 코 왼쪽(-X) 띠의 노멀이 빛 반대쪽으로 누워 있다.
        // 기준 빛(수평 35°)에서 임계값 0 으로도 그림자가 넉넉히 생긴다.
        static FaceLandmarks.Src NoseFace()
        {
            const float step = 0.005f; const int nx = 25, ny = 21;   // x -0.06~0.06, y -0.08~0.02 (코끝 자리에 정점이 오도록)
            var w = new List<Vector3>(); var n = new List<Vector3>(); var tri = new List<int>();
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    float x = -0.06f + i * step, y = -0.08f + j * step;
                    bool tip = Mathf.Abs(x) < 1e-4f && Mathf.Abs(y + 0.03f) < 1e-4f;
                    w.Add(new Vector3(x, y, tip ? 0.06f : 0.05f));
                    bool side = x >= -0.0151f && x < -1e-4f && y >= -0.0451f && y <= -0.0149f;
                    n.Add(side ? new Vector3(-0.9f, 0f, 0.436f).normalized : Vector3.forward);
                }
            for (int j = 0; j + 1 < ny; j++)
                for (int i = 0; i + 1 < nx; i++)
                {
                    int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                    tri.AddRange(new[] { a, c, b, b, c, d });
                }
            // 얼굴 바운드(가로 16cm, 세로 24cm, 깊이 14cm)를 만드는 점. 삼각형에는 쓰지 않는다.
            w.Add(new Vector3(-0.08f, -0.12f, -0.08f)); n.Add(Vector3.forward);
            w.Add(new Vector3(0.08f, 0.12f, -0.08f)); n.Add(Vector3.forward);
            var weights = new float[w.Count]; for (int i = 0; i < weights.Length; i++) weights[i] = 1f;
            return new FaceLandmarks.Src { name = "Head", world = w.ToArray(), weights = weights, isFace = true, normals = n.ToArray(), triangles = tri.ToArray() };
        }

        [Test]
        public void CrescentIsFittedEvenWhenTheNoseHasForm()
        {
            var parts = new List<FaceLandmarks.Src> { NoseFace(), Eyes() };
            var m = FaceLandmarks.Detect(parts);
            Assert.That(m.noseTip, Is.EqualTo(new Vector3(0f, -0.03f, 0.06f)).Using(Vector3EqualityComparer.Instance), "가짜 얼굴의 코끝");
            var light = FBX2NPRConverter.DefaultLight();
            m.CalibrateNoseStop(parts, light);
            Assert.That(m.noseStop, Is.EqualTo(0f), "코에 형태가 있어 임계값을 올리지 않는다");
            m.FitNoseMoon(parts, light);
            Assert.That(m.moonOn, Is.True, "그래도 코 그림자는 원본 노멀 그림자 자리에 맞춘 도형으로 그린다");
            Assert.That(m.moonShape.z, Is.GreaterThan(0f));
        }
    }
}
