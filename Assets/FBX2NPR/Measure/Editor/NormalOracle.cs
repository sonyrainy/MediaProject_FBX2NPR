using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 유니티에서 구현한 방사형 정렬이 블렌더 Normal Edit / RADIAL 과 같은 값을 내는지 검증합니다.
    ///
    /// 블렌더로 mix 0.0 ~ 1.0 을 0.2 간격으로 미리 구워 둔 FBX 6개가 정답지입니다.
    /// 유니티는 mix=0 파일 하나만 읽고 나머지 다섯 개를 계산으로 만들어 낸 뒤,
    /// 정답지와 정점 단위로 각도를 비교합니다.
    /// 여기서 0도에 수렴해야 DCC 왕복을 걷어낼 근거가 생깁니다.
    /// </summary>
    public static class NormalOracle
    {
        const string Folder = "Assets/NormalTest";
        static readonly int[] Levels = { 0, 20, 40, 60, 80, 100 };

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Debug.Log("[oracle] " + m); Console.WriteLine("[oracle] " + m); }

        [MenuItem("FBX2NPR/블렌더 정답지와 대조")]
        public static void Verify()
        {
            Log.Clear();
            try
            {
                // 탄젠트를 끄는 이유: 켜면 구형 쪽만 정점이 1개 더 쪼개져서
                // 인덱스 대 인덱스 비교가 성립하지 않습니다.
                foreach (int lv in Levels) SetImport(Path(lv));

                Mesh baseMesh = Load(Path(0));
                if (baseMesh == null) { Finish(); return; }

                Vector3[] verts = baseMesh.vertices;
                Vector3[] baseN = baseMesh.normals;
                Vector3 c = RadialNormals.Centroid(verts);
                L(string.Format(CultureInfo.InvariantCulture,
                    "기준 메시 정점 {0}   유니티가 계산한 중심 ({1:F5}, {2:F5}, {3:F5})",
                    verts.Length, c.x, c.y, c.z));
                L("");
                L("  mix    정점      평균오차      중앙오차      최대오차     판정");

                bool allOk = true;
                foreach (int lv in Levels)
                {
                    Mesh truth = Load(Path(lv));
                    if (truth == null) { allOk = false; continue; }
                    if (truth.vertexCount != verts.Length)
                    {
                        L(string.Format("  {0,3}    정점 수 불일치 {1} vs {2} — 비교 불가",
                            lv, truth.vertexCount, verts.Length));
                        allOk = false; continue;
                    }

                    Vector3[] mine = RadialNormals.Apply(verts, baseN, c, lv / 100f);
                    Vector3[] t = truth.normals;

                    // 합격/불합격만 찍지 않고 꼬리를 규정합니다.
                    // 허용오차를 논문에 적으려면 "몇 개가 얼마나 벗어나는지"가 있어야 합니다.
                    int over01 = 0, over05 = 0, over10 = 0;
                    float mean, med, max;
                    RadialNormals.Compare(mine, t, out mean, out med, out max);
                    int worst = 0; float worstAng = -1f;
                    for (int i = 0; i < t.Length; i++)
                    {
                        float d = Mathf.Clamp(Vector3.Dot(mine[i].normalized, t[i].normalized), -1f, 1f);
                        float a = Mathf.Acos(d) * Mathf.Rad2Deg;
                        if (a > 0.1f) over01++;
                        if (a > 0.5f) over05++;
                        if (a > 1.0f) over10++;
                        if (a > worstAng) { worstAng = a; worst = i; }
                    }

                    bool ok = over05 == 0 || (100.0 * over05 / t.Length) < 0.1;
                    if (!ok) allOk = false;
                    L(string.Format(CultureInfo.InvariantCulture,
                        "  {0,3}  평균 {1:F4}도  중앙 {2:F4}도  최대 {3:F4}도  |  0.1도초과 {4,3}  0.5도초과 {5,2}  1도초과 {6,2}  |  {7}",
                        lv, mean, med, max, over01, over05, over10, ok ? "통과" : "실패"));

                    if (worstAng > 0.1f)
                    {
                        // 최악 정점이 중심에 붙어 있으면 방향 자체가 불안정한 게 원인입니다.
                        float rad = (verts[worst] - c).magnitude;
                        float meanRad = 0f;
                        for (int i = 0; i < verts.Length; i++) meanRad += (verts[i] - c).magnitude;
                        meanRad /= verts.Length;
                        L(string.Format(CultureInfo.InvariantCulture,
                            "        최악 정점 #{0}  오차 {1:F4}도  중심거리 {2:F5} (평균 {3:F5} 의 {4:F0}%)",
                            worst, worstAng, rad, meanRad, 100f * rad / meanRad));
                    }
                }

                L("");
                L(allOk
                    ? "=> 전부 일치. 유니티 구현만으로 블렌더를 대체할 수 있습니다."
                    : "=> 불일치 있음. 블렌더의 혼합 방식이 단순 Lerp 가 아닐 수 있습니다.");
            }
            catch (Exception e) { L("예외: " + e); }
            Finish();
        }

        static string Path(int lv) { return string.Format("{0}/Head_mix{1:000}.fbx", Folder, lv); }

        static void SetImport(string p)
        {
            var mi = AssetImporter.GetAtPath(p) as ModelImporter;
            if (mi == null) { L("임포터 없음: " + p); return; }
            if (mi.importNormals == ModelImporterNormals.Import
                && mi.importTangents == ModelImporterTangents.None
                && !mi.weldVertices && (int)mi.meshOptimizationFlags == 0) return;
            mi.importNormals         = ModelImporterNormals.Import;
            mi.importTangents        = ModelImporterTangents.None;
            mi.importBlendShapes     = false;
            mi.weldVertices          = false;
            mi.meshOptimizationFlags = 0;
            mi.meshCompression       = ModelImporterMeshCompression.Off;
            mi.isReadable            = true;
            mi.SaveAndReimport();
        }

        static Mesh Load(string p)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p)) { var m = o as Mesh; if (m != null) return m; }
            L("메시 없음: " + p);
            return null;
        }

        static void Finish()
        {
            string d = System.IO.Path.Combine(
                Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(d);
            File.WriteAllText(System.IO.Path.Combine(d, "log_oracle.txt"),
                Log.ToString(), new UTF8Encoding(true));
        }
    }
}