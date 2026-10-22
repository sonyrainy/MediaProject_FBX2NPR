using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 새 테스트 캐릭터가 유니티에 어떻게 들어오는지 실측합니다.
    /// 일반화 실험을 짜기 전에 알아야 하는 것: 메시 구성, 본 이름, 머리 가중치 분포,
    /// UV 채널 점유(원본 노멀을 UV3 에 심으므로), 텍스처 추출 결과.
    /// </summary>
    public static class ImportProbe
    {
        // Assets/TestSet 의 FBX 를 전부 잡습니다. 새 캐릭터는 파일만 넣으면 됩니다.
        static string[] Assets
        {
            get
            {
                var list = new List<string>();
                foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) list.Add(p);
                }
                list.Sort();
                return list.ToArray();
            }
        }

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Console.WriteLine("[probe] " + m); }

        [MenuItem("FBX2NPR/테스트셋 임포트 검사")]
        public static void Run()
        {
            Log.Clear();
            try
            {
                foreach (var path in Assets)
                {
                    L("");
                    L("==================== " + Path.GetFileName(path));
                    var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (mi == null) { L("  임포터 없음"); continue; }

                    mi.importNormals = ModelImporterNormals.Import;
                    mi.importTangents = ModelImporterTangents.CalculateMikk;
                    mi.weldVertices = false;
                    mi.meshOptimizationFlags = 0;
                    mi.meshCompression = ModelImporterMeshCompression.Off;
                    mi.isReadable = true;
                    mi.importBlendShapes = false;
                    mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                    mi.SaveAndReimport();

                    // 텍스처는 FBX 안에 packed. 꺼내야 머티리얼이 참조합니다.
                    string texDir = Path.GetDirectoryName(path) + "/" + Path.GetFileNameWithoutExtension(path) + "_tex";
                    if (!AssetDatabase.IsValidFolder(texDir))
                    {
                        AssetDatabase.CreateFolder(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + "_tex");
                        bool ok = mi.ExtractTextures(texDir);
                        L("  텍스처 추출: " + (ok ? "성공" : "실패/없음") + " -> " + texDir);
                        AssetDatabase.Refresh();
                        mi.SaveAndReimport();
                    }

                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go == null) { L("  프리팹 로드 실패"); continue; }

                    var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                    var mfs = go.GetComponentsInChildren<MeshFilter>(true);
                    L(string.Format("  루트 스케일 {0}  SMR {1}개  정적 MeshFilter {2}개",
                        go.transform.localScale, smrs.Length, mfs.Length));

                    int totalV = 0, totalHead = 0;
                    foreach (var smr in smrs)
                    {
                        var m = smr.sharedMesh; if (m == null) continue;
                        totalV += m.vertexCount;

                        // 머리 본 인덱스
                        var heads = new HashSet<int>(); var headNames = new List<string>();
                        for (int i = 0; i < smr.bones.Length; i++)
                        {
                            var b = smr.bones[i]; if (b == null) continue;
                            if (b.name.ToLowerInvariant().Contains("head")) { heads.Add(i); headNames.Add(b.name); }
                        }
                        int headV = 0; int headV05 = 0;
                        var bw = m.boneWeights;
                        if (bw != null && bw.Length == m.vertexCount)
                        {
                            for (int i = 0; i < bw.Length; i++)
                            {
                                float w = 0f;
                                if (heads.Contains(bw[i].boneIndex0)) w += bw[i].weight0;
                                if (heads.Contains(bw[i].boneIndex1)) w += bw[i].weight1;
                                if (heads.Contains(bw[i].boneIndex2)) w += bw[i].weight2;
                                if (heads.Contains(bw[i].boneIndex3)) w += bw[i].weight3;
                                if (w > 0f) headV++;
                                if (w >= 0.5f) headV05++;
                            }
                        }
                        totalHead += headV05;

                        // UV 채널 점유
                        var uvOcc = new StringBuilder();
                        for (int c = 0; c < 8; c++)
                        {
                            var tmp = new List<Vector4>();
                            m.GetUVs(c, tmp);
                            if (tmp.Count > 0) uvOcc.Append(c).Append(' ');
                        }

                        var mats = new StringBuilder();
                        foreach (var mat in smr.sharedMaterials)
                        {
                            if (mat == null) { mats.Append("null "); continue; }
                            var t = mat.mainTexture;
                            mats.Append(mat.name).Append('[').Append(t == null ? "no-tex" : (t.name + " " + t.width + "x" + t.height)).Append("] ");
                        }

                        L(string.Format("  SMR {0,-28} v={1,6} sub={2} bones={3,2} head본={4}  head>0:{5,6}  head>=0.5:{6,6} ({7,5:F1}%)  uv={8} mat={9}",
                            m.name, m.vertexCount, m.subMeshCount, smr.bones.Length,
                            string.Join("|", headNames.ToArray()), headV, headV05,
                            100.0 * headV05 / Mathf.Max(1, m.vertexCount), uvOcc.ToString().Trim(), mats.ToString().Trim()));
                        L(string.Format("      bounds center {0} size {1}  localScale {2}",
                            m.bounds.center, m.bounds.size, smr.transform.lossyScale));
                    }
                    foreach (var mf in mfs)
                    {
                        var m = mf.sharedMesh; if (m == null) continue;
                        var mr = mf.GetComponent<MeshRenderer>();
                        var chain = new List<string>(); for (var t = mf.transform.parent; t != null && t != go.transform; t = t.parent) chain.Add(t.name);
                        chain.Reverse();
                        L(string.Format("  MF  {0,-28} v={1,6} sub={2} mat={3}  부모={4}", m.name, m.vertexCount, m.subMeshCount,
                            mr == null ? "-" : string.Join("|", Array.ConvertAll(mr.sharedMaterials, x => x == null ? "null" : x.name)), string.Join("/", chain.ToArray())));
                    }
                    L(string.Format("  합계 정점 {0}  머리(>=0.5) {1} ({2:F1}%)", totalV, totalHead, 100.0 * totalHead / Mathf.Max(1, totalV)));
                }
            }
            catch (Exception ex) { L("예외: " + ex); }
            string d = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut");
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "log_probe.txt"), Log.ToString(), new UTF8Encoding(true));
        }
    }
}
