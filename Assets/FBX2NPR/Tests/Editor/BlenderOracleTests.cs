using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 블렌더 Normal Edit / RADIAL 로 mix 0~1 을 0.2 간격으로 구워 둔 정답지와 정점 단위로 대조합니다.
    /// NormalOracle 메뉴와 같은 파일, 같은 합격선(0.5도 넘는 정점이 0.1% 미만)입니다.
    /// 기준선 (NormalTestOut/log_oracle.txt): 평균 0.002~0.004도, 최대 0.79도, 0.5도 초과 0~2개 / 5383개.
    /// 정답지는 믹사모 모델에서 나와 저장소에 올리지 않으므로, 파일이 없으면 건너뜁니다.
    /// </summary>
    public class BlenderOracleTests
    {
        const string Folder = "Assets/NormalTest";

        static Mesh Load(int level)
        {
            string p = string.Format("{0}/Head_mix{1:000}.fbx", Folder, level);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p)) { var m = o as Mesh; if (m != null) return m; }
            return null;
        }

        [TestCase(0)]
        [TestCase(20)]
        [TestCase(40)]
        [TestCase(60)]
        [TestCase(80)]
        [TestCase(100)]
        public void MatchesBlenderNormalEdit(int level)
        {
            Mesh baseMesh = Load(0), truth = Load(level);
            if (baseMesh == null || truth == null) Assert.Ignore("블렌더 정답지가 없습니다 (" + Folder + ", 저장소 밖 파일)");
            Assert.That(truth.vertexCount, Is.EqualTo(baseMesh.vertexCount), "정점 수가 다르면 인덱스 대조가 안 됩니다. 정답지 임포트 설정(정점 합치기·최적화 끔)을 확인하세요.");

            Vector3[] verts = baseMesh.vertices;
            Vector3[] mine = RadialNormals.Apply(verts, baseMesh.normals, RadialNormals.Centroid(verts), level / 100f);
            Vector3[] t = truth.normals;

            float mean, median, max;
            RadialNormals.Compare(mine, t, out mean, out median, out max);
            int over05 = 0;
            for (int i = 0; i < t.Length; i++)
            {
                float d = Mathf.Clamp(Vector3.Dot(mine[i].normalized, t[i].normalized), -1f, 1f);
                if (Mathf.Acos(d) * Mathf.Rad2Deg > 0.5f) over05++;
            }

            Assert.That(mean, Is.LessThan(0.01f), "평균 오차(도)");
            Assert.That(max, Is.LessThan(1f), "최대 오차(도)");
            Assert.That(over05, Is.LessThan(t.Length * 0.001f), "0.5도 넘는 정점 수");
        }
    }
}
