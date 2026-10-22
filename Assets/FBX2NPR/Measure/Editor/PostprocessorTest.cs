using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 실제 믹사모 전신 FBX 로 임포터를 검증합니다.
    ///
    /// 설정을 끈 상태로 한 번, 켠 상태로 한 번 임포트해서 메시별로 노멀이 어떻게 변했는지 잽니다.
    /// 확인하려는 것은 두 가지입니다.
    ///   (1) 머리가 실린 메시만 바뀌고 나머지는 그대로인가
    ///   (2) 바뀐 양이 mix 값에 걸맞은가
    /// 눈으로 보고 "잘 된 것 같다"로 넘어가지 않기 위해 숫자로 받습니다.
    /// </summary>
    public static class PostprocessorTest
    {
        const string Asset = "Assets/FBX2NPR/Sample/Ch07.fbx";
        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Debug.Log("[pptest] " + m); Console.WriteLine("[pptest] " + m); }

        [MenuItem("FBX2NPR/임포터 검증 (전신 FBX)")]
        public static void Run()
        {
            Log.Clear();
            try
            {
                var imp = AssetImporter.GetAtPath(Asset) as ModelImporter;
                if (imp == null) { L("에셋을 찾지 못함: " + Asset); Finish(); return; }

                // 두 번의 임포트가 반드시 같은 임포터 설정을 써야 합니다.
                // 처음엔 enabled 만 바꿨는데, 변환이 켜지면 OnPreprocessModel 이 Weld 와
                // Optimize Mesh 를 끕니다. 기준선은 기본값(Optimize=Everything)으로 들어왔고
                // 그건 정점을 재정렬하므로, 인덱스 대조가 전혀 다른 정점끼리 맞대게 됩니다.
                // 실제로 바지·신발까지 90도씩 변한 것처럼 나왔습니다.
                L("--- 1) 변환 끈 상태 (기준선) ---");
                var off = new FBX2NPRAssetSettings { enabled = false };
                imp.userData = off.ToJson();
                FixImportSettings(imp);
                imp.SaveAndReimport();
                var baseline = Snapshot();
                foreach (var kv in baseline)
                    L(string.Format("    {0,-18} 정점 {1,6}", kv.Key, kv.Value.Length));

                L("");
                L("--- 2) 변환 켠 상태 (mix 0.40) ---");
                var on = new FBX2NPRAssetSettings { enabled = true, mix = 0.4f, normalMapStrength = 0f };
                imp = AssetImporter.GetAtPath(Asset) as ModelImporter;
                imp.userData = on.ToJson();
                FixImportSettings(imp);
                imp.SaveAndReimport();
                var after = Snapshot();

                L("");
                L("--- 3) 메시별 노멀 변화 ---");
                L("    메시                 정점    변경비율    평균      최대");
                foreach (var kv in baseline)
                {
                    Vector3[] a = kv.Value, b;
                    if (!after.TryGetValue(kv.Key, out b)) { L("    " + kv.Key + "  사라짐"); continue; }
                    if (a.Length != b.Length)
                    {
                        L(string.Format("    {0,-18} 정점 수 불일치 {1} -> {2}", kv.Key, a.Length, b.Length));
                        continue;
                    }
                    int moved = 0; double sum = 0; float max = 0f;
                    for (int i = 0; i < a.Length; i++)
                    {
                        float d = Mathf.Clamp(Vector3.Dot(a[i].normalized, b[i].normalized), -1f, 1f);
                        float t = Mathf.Acos(d) * Mathf.Rad2Deg;
                        sum += t; if (t > max) max = t; if (t > 1f) moved++;
                    }
                    L(string.Format(CultureInfo.InvariantCulture,
                        "    {0,-18} {1,6}   {2,6:F1}%   {3,7:F2}도 {4,7:F2}도",
                        kv.Key, a.Length, 100.0 * moved / a.Length, sum / a.Length, max));
                }

                L("");
                L("--- 4) 판정 ---");
                L("    머리 가중치가 실린 메시만 변해야 합니다 (Body / Suit / Eyelashes / Hair).");
                L("    Pants / Heels / Shirt 가 0.0% 여야 통과입니다.");
            }
            catch (Exception e) { L("예외: " + e); }
            Finish();
        }

        /// <summary>
        /// 두 번의 임포트가 동일 조건이 되도록 못 박습니다.
        /// 하나라도 다르면 정점 순서나 개수가 달라져 비교 자체가 무의미해집니다.
        /// </summary>
        static void FixImportSettings(ModelImporter mi)
        {
            mi.importNormals         = ModelImporterNormals.Import;
            mi.importTangents        = ModelImporterTangents.CalculateMikk;
            mi.weldVertices          = false;
            mi.meshOptimizationFlags = 0;
            mi.meshCompression       = ModelImporterMeshCompression.Off;
            mi.importBlendShapes     = false;
            mi.isReadable            = true;
        }

        static Dictionary<string, Vector3[]> Snapshot()
        {
            var d = new Dictionary<string, Vector3[]>();
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Asset))
            {
                var m = o as Mesh;
                if (m == null) continue;
                if (!d.ContainsKey(m.name)) d[m.name] = m.normals;
            }
            return d;
        }

        static void Finish()
        {
            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "log_pptest.txt"), Log.ToString(), new UTF8Encoding(true));
        }
    }
}