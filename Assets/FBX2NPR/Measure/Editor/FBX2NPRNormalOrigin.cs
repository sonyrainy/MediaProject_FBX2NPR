using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 얼굴 노멀의 출처 판정 (읽기 전용).
    /// 파일에서 임포트된 노멀이 '형상에서 다시 계산한 보통 매끈한 노멀'과 같은지,
    /// 아니면 제작자가 툰용으로 손본(편집한) 노멀인지를 수치로 잰다.
    /// 에셋·재질·임포트 설정은 읽기만 하고 바꾸지 않는다. 결과는 NormalTestOut/normal_origin/.
    /// </summary>
    public static class FBX2NPRNormalOrigin
    {
        const string OutDir = "NormalTestOut/normal_origin";
        const float SynthMix = 0.4f;   // 양성 대조: FBX2NPR 기본 mix 와 같은 세기로 방사형 편집을 흉내 낸다

        static readonly StringBuilder Log = new StringBuilder();
        static readonly StringBuilder Csv = new StringBuilder();
        static readonly List<Row> Rows = new List<Row>();
        static void L(string m) { Log.AppendLine(m); Console.WriteLine("[norigin] " + m); }

        // ---------------------------------------------------------------- 자료형

        class Src
        {
            public string name;
            public Vector3[] pos;   // 월드
            public Vector3[] nrm;   // 월드, 단위 벡터 (임포트 노멀)
            public int[] tris;      // 음수 행렬식이면 와인딩을 뒤집어 둠
            public int[] triSub;
            public int[] srcOf;     // 병합 소스일 때 정점별 원래 메시 번호
            public string[] srcNames;
            public float size;      // 경계 상자 대각선 (월드)
        }

        class Rec
        {
            public int gCount;
            public Vector3[] angN, areaN;
            public bool[] valid, open;
            public List<int>[] gVerts, gCorners;
            public Vector3[] fn;
            public float[] cornerAng;
            public List<int>[] vertTris;
            public List<long> edges = new List<long>();
            public int degenerate, cancelled;
            // 다각형(사각형 복원) 기준 재계산: 연속한 두 삼각형이 변을 공유하면 원래 사각형으로 보고 Newell 노멀·사각형 내각을 쓴다.
            public Vector3[] polyAngN, polyAreaN, triUnwN, polyUnwN;
            public int quads;
        }

        class St { public int n; public double mean, median, p90, max, f5, f15, f01; }

        class Row
        {
            public string model, mesh, region, subset;
            public int verts, valid, groups, multi, split1, split30, open, flipped;
            public St angW, areaW, crease, polyAng, polyArea, triUnw, polyUnw;
            public double[] radImp = new double[3], radRec = new double[3];
            public double spreadImp, spreadRec, roughImp, roughRec;
            public int edgeCount;
        }

        // ---------------------------------------------------------------- 진입점

        [MenuItem("FBX2NPR/진단/노멀 출처 판정")]
        public static void Run()
        {
            Log.Clear(); Csv.Clear(); Rows.Clear();
            Csv.AppendLine("model,mesh,region,subset,verts,valid,meanAngW,meanAreaW,meanCrease60,medianAngW,p90AngW,maxAngW,fracOver5,fracOver15,groups,multiGroups,split1deg,split30deg,openBoundaryVerts,flipped,radImpC1,radRecC1,radImpC2,radRecC2,radImpC3,radRecC3,spreadImp,spreadRec,roughImp,roughRec,edges,meanPolyAng,p90PolyAng,meanPolyArea");
            Directory.CreateDirectory(OutDir);
            L("노멀 출처 판정  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  Unity " + Application.unityVersion);
            L("각도는 모두 도(°). '재계산' = 같은 위치 정점을 묶어 그 위치를 공유하는 모든 삼각형 면 노멀을 평균한 매끈한 노멀.");
            L("  각도가중 = 꼭짓점 내각 가중, 면적가중 = 외적(면적×2) 가중, 꺾임60 = 자기 면과 60° 안쪽 면만 (Unity 계산 모드 흉내).");
            L("  방사각 = 노멀과 (정점 - 중심) 방향의 각. C1 = 영역 정점 중심, C2 = 영역 경계상자 중심을 앞뒤 깊이 절반만큼 뒤로 민 점, C3 = 구 맞춤 중심.");
            L("  퍼짐 = 영역 평균 노멀과의 평균 각. 인접 변 노멀차 = 영역 안 변 양끝 위치의 노멀 각 (작을수록 매끈).");
            try
            {
                UnityChan("Assets/TestSet/UnityChan/Models/unitychan.fbx");
                Mixamo("Assets/TestSet/Ch07.fbx", null);
                Mixamo("Assets/TestSet/TheBoss.fbx", "Head_Geo");
                Mixamo("Assets/TestSet/Jones.fbx", null);
                Summary();
            }
            catch (Exception e) { L("예외: " + e); }
            finally
            {
                L("DONE");
                File.WriteAllText(Path.Combine(OutDir, "normal_origin.log"), Log.ToString(), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(OutDir, "normal_origin.csv"), Csv.ToString(), new UTF8Encoding(true));
            }
        }

        // ---------------------------------------------------------------- 유니티짱

        static void UnityChan(string path)
        {
            L(""); L("==================== " + path);
            LogImporter(path);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { L("  로드 실패"); return; }

            var rends = go.GetComponentsInChildren<Renderer>(true);
            var srcs = new List<Src>();
            var regions = new List<List<int>>();
            var rendOf = new List<Renderer>();
            foreach (var r in rends)
            {
                Mesh m = null;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) m = smr.sharedMesh;
                else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) m = mf.sharedMesh; }
                if (m == null) continue;
                var mats = r.sharedMaterials;
                var s = MakeSrc(r.transform, m);
                var sb = new StringBuilder();
                var region = new HashSet<int>();
                for (int sub = 0; sub < m.subMeshCount; sub++)
                {
                    string mn = sub < mats.Length && mats[sub] != null ? mats[sub].name : "(없음)";
                    var sm = m.GetSubMesh(sub);
                    sb.Append(" [" + sub + ":" + mn + " 삼각형 " + (sm.topology == MeshTopology.Triangles ? (sm.indexCount / 3).ToString() : sm.topology.ToString()) + "]");
                    if (mn.ToLowerInvariant().Contains("face") && sm.topology == MeshTopology.Triangles)
                        foreach (var idx in m.GetTriangles(sub)) region.Add(idx);
                }
                bool underHead = false;
                for (var t = r.transform; t != null; t = t.parent) if (t.name.ToLowerInvariant().Contains("head")) { underHead = true; break; }
                L(string.Format("  {0} '{1}' 경로={2} 메시={3} v={4} 서브메시={5} 활성={6}/{7} 머리아래={8} 크기={9:F4} 스케일={10} 행렬식={11:F4}{12}",
                    smr != null ? "SMR" : "MR", r.name, HPath(r.transform, go.transform), m.name, m.vertexCount, m.subMeshCount,
                    r.gameObject.activeSelf, r.enabled, underHead ? "예" : "아니오", s.size, r.transform.lossyScale.ToString("F4"),
                    r.transform.localToWorldMatrix.determinant, sb.ToString()));
                if (s.nrm == null) { L("    노멀 없음"); continue; }
                srcs.Add(s); rendOf.Add(r);
                var rl = new List<int>(region); rl.Sort();
                regions.Add(rl);
            }

            // 얼굴 재질 영역이 있는 메시마다, 같은 메시 안에서만 묶어 잰다.
            float faceSize = 0f;
            {
                var bmin = Vector3.positiveInfinity; var bmax = Vector3.negativeInfinity; bool any = false;
                for (int k = 0; k < srcs.Count; k++) foreach (var i in regions[k]) { bmin = Vector3.Min(bmin, srcs[k].pos[i]); bmax = Vector3.Max(bmax, srcs[k].pos[i]); any = true; }
                if (any) faceSize = (bmax - bmin).magnitude;
            }
            L(string.Format("  얼굴 재질('face' 포함) 영역 전체 경계상자 대각선 {0:F4}", faceSize));

            var perMeshAngN = new Dictionary<int, Vector3[]>();
            for (int k = 0; k < srcs.Count; k++)
            {
                if (regions[k].Count == 0) continue;
                var s = srcs[k];
                L(""); L("  ---- 메시 " + s.name + " (메시 안에서만 묶음)  얼굴 재질 정점 " + regions[k].Count);
                float tol = 1e-6f * s.size;
                int gc; var gid = Group(s.pos, tol, out gc);
                int gc4; Group(s.pos, 1e-4f * s.size, out gc4);
                L(string.Format("    위치 묶음: 허용오차 {0:E2} -> {1}개 (1e-4×크기에서는 {2}개), 정점 {3}", tol, gc, gc4, s.pos.Length));
                var rec = Recompute(s, gid, gc);
                {
                    var pv = new Vector3[s.pos.Length];
                    for (int i = 0; i < pv.Length; i++) pv[i] = rec.valid[gid[i]] ? rec.angN[gid[i]] : Vector3.zero;
                    perMeshAngN[k] = pv;
                }
                L(string.Format("    삼각형 {0}, 퇴화 {1}, 상쇄(앞뒤 겹면 의심) 묶음 {2}", s.tris.Length / 3, rec.degenerate, rec.cancelled));
                var centers = Centers(s, regions[k], s.nrm);
                var interior = new List<int>(); foreach (var i in regions[k]) if (!rec.open[gid[i]]) interior.Add(i);
                Measure("unitychan", s.name, "face재질", "all", s, s.nrm, gid, rec, regions[k], centers, null);
                Measure("unitychan", s.name, "face재질", "interior", s, s.nrm, gid, rec, interior, centers, null);

                // 다른 메시와 위치가 겹치는 경계 정점 수
                foreach (float rel in new[] { 1e-6f, 1e-5f, 1e-4f })
                {
                    float t2 = rel * Mathf.Max(faceSize, s.size);
                    var hit = new Dictionary<string, int>(); int overl = 0;
                    var hash = new Dictionary<(long, long, long), List<Vector3>>();
                    double inv = 1.0 / t2;
                    for (int o = 0; o < srcs.Count; o++)
                    {
                        if (o == k) continue;
                        foreach (var p in srcs[o].pos)
                        {
                            var key = Cell(p, inv);
                            if (!hash.TryGetValue(key, out var lst)) { lst = new List<Vector3>(); hash[key] = lst; }
                            lst.Add(p);
                        }
                    }
                    var owner = new Dictionary<Vector3, string>();
                    for (int o = 0; o < srcs.Count; o++) if (o != k) foreach (var p in srcs[o].pos) owner[p] = srcs[o].name;
                    foreach (var i in regions[k])
                    {
                        var p = s.pos[i]; var c = Cell(p, inv); bool found = false; string who = null;
                        for (int dx = -1; dx <= 1 && !found; dx++)
                            for (int dy = -1; dy <= 1 && !found; dy++)
                                for (int dz = -1; dz <= 1 && !found; dz++)
                                    if (hash.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var lst))
                                        foreach (var q in lst) if ((q - p).sqrMagnitude <= t2 * t2) { found = true; owner.TryGetValue(q, out who); break; }
                        if (found) { overl++; who = who ?? "?"; hit[who] = (hit.TryGetValue(who, out var cnt) ? cnt : 0) + 1; }
                    }
                    var parts = new List<string>(); foreach (var kv in hit) parts.Add(kv.Key + ":" + kv.Value);
                    L(string.Format("    다른 메시와 위치 겹침 (허용 {0:E0}×{1:F3}={2:E2}): 얼굴 재질 정점 {3}/{4}  {5}", rel, Mathf.Max(faceSize, s.size), t2, overl, regions[k].Count, string.Join(" ", parts.ToArray())));
                }
            }

            // 모든 메시를 월드에서 합쳐 다시 계산 (메시 경계 너머 삼각형까지 포함)
            {
                L(""); L("  ---- 병합: 모델의 모든 메시를 월드 공간에서 합쳐 묶음 (메시 경계 너머 면 포함)");
                var merged = Merge("unitychan_merged", srcs);
                var region = new List<int>();
                int off = 0;
                for (int k = 0; k < srcs.Count; k++) { foreach (var i in regions[k]) region.Add(off + i); off += srcs[k].pos.Length; }
                foreach (float rel in new[] { 1e-6f, 1e-5f })
                {
                    float tol = rel * faceSize;
                    int gc; var gid = Group(merged.pos, tol, out gc);
                    var rec = Recompute(merged, gid, gc);
                    L(string.Format("    허용오차 {0:E0}×얼굴크기 = {1:E2}: 묶음 {2}, 퇴화 {3}, 상쇄 {4}", rel, tol, gc, rec.degenerate, rec.cancelled));
                    // 얼굴 영역 묶음에 면을 보탠 메시
                    var contrib = new Dictionary<string, int>(); var regionG = new HashSet<int>();
                    foreach (var i in region) regionG.Add(gid[i]);
                    foreach (var g in regionG) foreach (var corner in rec.gCorners[g])
                        {
                            int t = corner / 3; string nm = merged.srcNames[merged.srcOf[merged.tris[3 * t]]];
                            contrib[nm] = (contrib.TryGetValue(nm, out var c) ? c : 0) + 1;
                        }
                    var parts = new List<string>(); foreach (var kv in contrib) parts.Add(kv.Key + ":" + kv.Value);
                    L("    얼굴 영역 위치에 닿는 면 모서리(메시별): " + string.Join(" ", parts.ToArray()));
                    if (rel > 2e-6f)
                    {
                        var centers = Centers(merged, region, merged.nrm);
                        var interior = new List<int>(); foreach (var i in region) if (!rec.open[gid[i]]) interior.Add(i);
                        Measure("unitychan", "merged", "face재질", "all", merged, merged.nrm, gid, rec, region, centers, null);
                        Measure("unitychan", "merged", "face재질", "interior", merged, merged.nrm, gid, rec, interior, centers, null);
                        Synthetic("unitychan", "merged", "face재질", merged, gid, rec, region, centers);

                        // 보이는 얼굴만 (EYE_DEF + MTH_DEF). head_back 은 머리카락에 덮이는 뒤통수·턱 밑.
                        var visible = new List<int>();
                        foreach (var i in region) { string nm = merged.srcNames[merged.srcOf[i]]; if (nm == "EYE_DEF" || nm == "MTH_DEF") visible.Add(i); }
                        var vCenters = Centers(merged, visible, merged.nrm);
                        var vInterior = new List<int>(); foreach (var i in visible) if (!rec.open[gid[i]]) vInterior.Add(i);
                        Measure("unitychan", "merged EYE+MTH", "face재질(보이는 얼굴)", "all", merged, merged.nrm, gid, rec, visible, vCenters, null);
                        Measure("unitychan", "merged EYE+MTH", "face재질(보이는 얼굴)", "interior", merged, merged.nrm, gid, rec, vInterior, vCenters, null);
                        Synthetic("unitychan", "merged EYE+MTH", "face재질(보이는 얼굴)", merged, gid, rec, visible, vCenters);

                        // 메시 이음매: 서로 다른 메시의 정점이 같은 위치에 있을 때 임포트 노멀이 서로 맞는가
                        var srcStart = new int[srcs.Count]; { int o2 = 0; for (int k = 0; k < srcs.Count; k++) { srcStart[k] = o2; o2 += srcs[k].pos.Length; } }
                        int seamG = 0; double seamSum = 0, seamMax = 0; var seamDevMerged = new List<double>(); var seamDevPer = new List<double>(); var keyM = new Dictionary<string, List<double>>(); var keyP = new Dictionary<string, List<double>>();
                        var seamPairs = new Dictionary<string, int>();
                        foreach (var g in regionG)
                        {
                            var mem = rec.gVerts[g];
                            var ss = new HashSet<int>(); foreach (var i in mem) ss.Add(merged.srcOf[i]);
                            if (ss.Count < 2) continue;
                            seamG++;
                            double mx = 0;
                            for (int a = 0; a < mem.Count; a++) for (int b = a + 1; b < mem.Count; b++)
                                    if (merged.srcOf[mem[a]] != merged.srcOf[mem[b]]) mx = Math.Max(mx, Ang(merged.nrm[mem[a]], merged.nrm[mem[b]]));
                            seamSum += mx; seamMax = Math.Max(seamMax, mx);
                            var nl = new List<string>(); foreach (var sidx in ss) nl.Add(merged.srcNames[sidx]); nl.Sort();
                            string key = string.Join("+", nl.ToArray()) + (mx > 1.0 ? " (노멀 다름>1°)" : " (노멀 같음≤1°)");
                            seamPairs[key] = (seamPairs.TryGetValue(key, out var cc) ? cc : 0) + 1;
                            foreach (var i in mem)
                            {
                                int k = merged.srcOf[i];
                                if (!perMeshAngN.ContainsKey(k) || !rec.valid[g]) continue;
                                var pn = perMeshAngN[k][i - srcStart[k]];
                                if (pn.sqrMagnitude == 0f) continue;
                                double dm = Ang(merged.nrm[i], rec.angN[g]), dp = Ang(merged.nrm[i], pn); seamDevMerged.Add(dm); seamDevPer.Add(dp); if (!keyM.ContainsKey(key)) { keyM[key] = new List<double>(); keyP[key] = new List<double>(); } keyM[key].Add(dm); keyP[key].Add(dp);
                            }
                        }
                        var parts2 = new List<string>(); foreach (var kv in seamPairs) parts2.Add(kv.Key + ":" + kv.Value);
                        var smM = Stats(seamDevMerged); var smP = Stats(seamDevPer);
                        L(string.Format("    메시 이음매 위치 {0}개: 서로 다른 메시 임포트 노멀 사이 각 평균 {1:F2} 최대 {2:F2}  [{3}]",
                            seamG, seamG > 0 ? seamSum / seamG : 0, seamMax, string.Join(", ", parts2.ToArray())));
                        L(string.Format("    이음매 정점({0}개)의 임포트 노멀 편차: 병합 재계산 대비 평균 {1:F2} (p90 {2:F2}) / 메시 안 재계산 대비 평균 {3:F2} (p90 {4:F2})",
                            smM.n, smM.mean, smM.p90, smP.mean, smP.p90));
                        foreach (var kv in keyM)
                        {
                            var a1 = Stats(kv.Value); var a2 = Stats(keyP[kv.Key]);
                            L(string.Format("      {0}: 정점 {1}  병합 재계산 대비 평균 {2:F2} (중앙 {3:F2}, 최대 {4:F2}) / 메시 안 재계산 대비 평균 {5:F2} (중앙 {6:F2}, 최대 {7:F2})",
                                kv.Key, a1.n, a1.mean, a1.median, a1.max, a2.mean, a2.median, a2.max));
                        }

                        // 편차 상위 정점 (보이는 얼굴): 어디서 어긋나는지
                        var order = new List<int>(); foreach (var i in visible) if (rec.valid[gid[i]]) order.Add(i);
                        order.Sort((x, y) => Ang(merged.nrm[y], rec.angN[gid[y]]).CompareTo(Ang(merged.nrm[x], rec.angN[gid[x]])));
                        L("    보이는 얼굴 편차 상위 20 정점 (위치는 C1 기준 mm, x+ = 모델 왼쪽, y+ = 위, z+ = 앞):");
                        for (int q = 0; q < Math.Min(20, order.Count); q++)
                        {
                            int i = order[q]; int g = gid[i];
                            var rp = (merged.pos[i] - vCenters[0]) * 1000f;
                            L(string.Format("      {0,-8} #{1,-4} 위치 ({2,6:F1},{3,6:F1},{4,6:F1})  편차 각도가중 {5,5:F1} / 다각형 {6,5:F1}  국소 꺾임 {7,5:F1}  열린경계 {8}  같은위치 정점 {9}",
                                merged.srcNames[merged.srcOf[i]], i - srcStart[merged.srcOf[i]], rp.x, rp.y, rp.z,
                                Ang(merged.nrm[i], rec.angN[g]), Ang(merged.nrm[i], rec.polyAngN[g]), Sharp(rec, g), rec.open[g] ? "예" : "아니오", rec.gVerts[g].Count));
                        }
                    }
                }
            }

            // 얼굴 재질 메시끼리만 병합 (눈알·몸 메시 제외). 원래 한 덩어리였다면 이음매까지 맞아야 한다.
            {
                L(""); L("  ---- 얼굴 재질 메시끼리만 병합 (EYE_DEF + MTH_DEF + head_back)");
                var list = new List<Src>(); var regs = new List<List<int>>();
                for (int k = 0; k < srcs.Count; k++) if (regions[k].Count > 0) { list.Add(srcs[k]); regs.Add(regions[k]); }
                var fm = Merge("unitychan_face", list);
                var visible = new List<int>(); var allFace = new List<int>(); int off = 0;
                for (int k = 0; k < list.Count; k++)
                {
                    foreach (var i in regs[k]) { allFace.Add(off + i); if (list[k].name == "EYE_DEF" || list[k].name == "MTH_DEF") visible.Add(off + i); }
                    off += list[k].pos.Length;
                }
                float tol = 1e-5f * faceSize;
                int gc; var gid = Group(fm.pos, tol, out gc);
                var rec = Recompute(fm, gid, gc);
                L(string.Format("    허용오차 {0:E2}: 묶음 {1}, 삼각형 {2}, 퇴화 {3}, 상쇄 {4}", tol, gc, fm.tris.Length / 3, rec.degenerate, rec.cancelled));
                var vc = Centers(fm, visible, fm.nrm);
                var vin = new List<int>(); foreach (var i in visible) if (!rec.open[gid[i]]) vin.Add(i);
                Measure("unitychan", "face-only EYE+MTH", "face재질(보이는 얼굴)", "all", fm, fm.nrm, gid, rec, visible, vc, null);
                Measure("unitychan", "face-only EYE+MTH", "face재질(보이는 얼굴)", "interior", fm, fm.nrm, gid, rec, vin, vc, null);
                var ac = Centers(fm, allFace, fm.nrm);
                Measure("unitychan", "face-only 3메시", "face재질 전체", "all", fm, fm.nrm, gid, rec, allFace, ac, null);

                // 삼각형 무가중 기준으로 남는 편차가 어디인지
                var order = new List<int>(); foreach (var i in visible) if (rec.valid[gid[i]]) order.Add(i);
                order.Sort((x, y) => Ang(fm.nrm[y], rec.triUnwN[gid[y]]).CompareTo(Ang(fm.nrm[x], rec.triUnwN[gid[x]])));
                int over05 = 0; foreach (var i in order) if (Ang(fm.nrm[i], rec.triUnwN[gid[i]]) > 0.5) over05++;
                L(string.Format("    보이는 얼굴에서 삼각형 무가중 재계산과 0.5° 넘게 다른 정점 {0}/{1}. 상위 12:", over05, order.Count));
                var start = new int[list.Count]; { int o2 = 0; for (int k = 0; k < list.Count; k++) { start[k] = o2; o2 += list[k].pos.Length; } }
                for (int q = 0; q < Math.Min(12, order.Count); q++)
                {
                    int i = order[q]; int g = gid[i];
                    var rp = (fm.pos[i] - vc[0]) * 1000f;
                    var srcSet = new HashSet<string>(); foreach (var j in rec.gVerts[g]) srcSet.Add(fm.srcNames[fm.srcOf[j]]);
                    L(string.Format("      {0,-8} #{1,-4} 위치 ({2,6:F1},{3,6:F1},{4,6:F1})mm  무가중 편차 {5,5:F1}  각도가중 편차 {6,5:F1}  국소 꺾임 {7,5:F1}  열린경계 {8}  같은위치 [{9}]",
                        fm.srcNames[fm.srcOf[i]], i - start[fm.srcOf[i]], rp.x, rp.y, rp.z, Ang(fm.nrm[i], rec.triUnwN[g]), Ang(fm.nrm[i], rec.angN[g]), Sharp(rec, g),
                        rec.open[g] ? "예" : "아니오", string.Join("+", new List<string>(srcSet).ToArray())));
                }
            }
        }

        // ---------------------------------------------------------------- 믹사모

        static void Mixamo(string path, string wholeMeshName)
        {
            string model = Path.GetFileNameWithoutExtension(path);
            L(""); L("==================== " + path);
            LogImporter(path);
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { L("  로드 실패"); return; }
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                L("  정적 메시 " + (mf.sharedMesh != null ? mf.sharedMesh.name : "(없음)") + " (측정 안 함)");

            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var m = smr.sharedMesh; if (m == null) continue;
                var bones = smr.bones;
                var headIdx = new HashSet<int>(); var headNames = new List<string>();
                for (int b = 0; b < bones.Length; b++)
                {
                    if (bones[b] == null) continue;
                    string bn = bones[b].name;
                    if (bn.Contains("Head") && !bn.Contains("HeadTop")) { headIdx.Add(b); headNames.Add(bn); }
                }
                var w = new float[m.vertexCount];
                var bpv = m.GetBonesPerVertex(); var all = m.GetAllBoneWeights();
                if (bpv.Length == m.vertexCount)
                {
                    int q = 0;
                    for (int v = 0; v < m.vertexCount; v++)
                        for (int j = 0; j < bpv[v]; j++) { var bw = all[q++]; if (headIdx.Contains(bw.boneIndex)) w[v] += bw.weight; }
                }
                var region = new List<int>(); for (int v = 0; v < w.Length; v++) if (w[v] >= 0.5f) region.Add(v);
                var mats = smr.sharedMaterials; var mn = new List<string>(); foreach (var mt in mats) mn.Add(mt != null ? mt.name : "(없음)");
                var s = MakeSrc(smr.transform, m);
                L(string.Format("  SMR '{0}' 메시={1} v={2} 서브메시={3} 재질=[{4}] 머리본=[{5}] 머리≥0.5 정점 {6} 크기={7:F4} 스케일={8} 행렬식={9:F4}",
                    smr.name, m.name, m.vertexCount, m.subMeshCount, string.Join(",", mn.ToArray()), string.Join(",", headNames.ToArray()), region.Count, s.size,
                    smr.transform.lossyScale.ToString("F4"), smr.transform.localToWorldMatrix.determinant));
                bool whole = wholeMeshName != null && m.name.EndsWith(wholeMeshName);
                if (region.Count == 0 && !whole) continue;
                if (s.nrm == null) { L("    노멀 없음"); continue; }

                float tol = 1e-6f * s.size;
                int gc; var gid = Group(s.pos, tol, out gc);
                int gc4; Group(s.pos, 1e-4f * s.size, out gc4);
                var rec = Recompute(s, gid, gc);
                L(string.Format("    위치 묶음: 허용오차 {0:E2} -> {1}개 (1e-4×크기에서는 {2}개), 정점 {3}, 삼각형 {4}, 퇴화 {5}, 상쇄 {6}",
                    tol, gc, gc4, s.pos.Length, s.tris.Length / 3, rec.degenerate, rec.cancelled));

                if (region.Count > 0)
                {
                    var centers = Centers(s, region, s.nrm);
                    var interior = new List<int>(); foreach (var i in region) if (!rec.open[gid[i]]) interior.Add(i);
                    Measure(model, m.name, "머리≥0.5", "all", s, s.nrm, gid, rec, region, centers, null);
                    Measure(model, m.name, "머리≥0.5", "interior", s, s.nrm, gid, rec, interior, centers, null);
                    // 앞쪽 절반: 유니티짱 얼굴 재질 영역과 비슷한 범위로 비교하기 위해
                    var front = FrontHalf(s, region);
                    var fCenters = Centers(s, front, s.nrm);
                    Measure(model, m.name, "머리≥0.5 앞쪽절반", "all", s, s.nrm, gid, rec, front, fCenters, null);
                    Synthetic(model, m.name, "머리≥0.5", s, gid, rec, region, centers);
                }
                if (whole)
                {
                    var allV = new List<int>(); for (int i = 0; i < s.pos.Length; i++) allV.Add(i);
                    var centers = Centers(s, allV, s.nrm);
                    var interior = new List<int>(); foreach (var i in allV) if (!rec.open[gid[i]]) interior.Add(i);
                    Measure(model, m.name, "메시전체", "all", s, s.nrm, gid, rec, allV, centers, null);
                    Measure(model, m.name, "메시전체", "interior", s, s.nrm, gid, rec, interior, centers, null);
                }
            }
        }

        // ---------------------------------------------------------------- 측정

        static Row Measure(string model, string mesh, string region, string subset, Src s, Vector3[] nrm, int[] gid, Rec rec, List<int> verts, Vector3[] centers, string tag)
        {
            var row = new Row { model = model, mesh = mesh, region = region, subset = tag ?? subset, verts = verts.Count };
            var dA = new List<double>(); var dR = new List<double>(); var dC = new List<double>();
            var dPA = new List<double>(); var dPR = new List<double>(); var dTU = new List<double>(); var dPU = new List<double>();
            var sharpBins = new double[3]; var sharpCnt = new int[3]; var sharpCache = new Dictionary<int, double>();
            var regionG = new HashSet<int>();
            var gImp = new Dictionary<int, Vector3>();
            var meanImp = Vector3.zero; var meanRec = Vector3.zero;
            int nRad = 0;
            foreach (var i in verts)
            {
                int g = gid[i]; regionG.Add(g);
                Vector3 ni = nrm[i];
                gImp[g] = (gImp.TryGetValue(g, out var acc) ? acc : Vector3.zero) + ni;
                if (!rec.valid[g]) continue;
                Vector3 na = rec.angN[g], nr = rec.areaN[g];
                double devA = Ang(ni, na);
                dA.Add(devA); dR.Add(Ang(ni, nr));
                dPA.Add(Ang(ni, rec.polyAngN[g])); dPR.Add(Ang(ni, rec.polyAreaN[g])); dTU.Add(Ang(ni, rec.triUnwN[g])); dPU.Add(Ang(ni, rec.polyUnwN[g]));
                if (!sharpCache.TryGetValue(g, out var sh)) { sh = Sharp(rec, g); sharpCache[g] = sh; }
                int bin = sh < 15 ? 0 : sh < 40 ? 1 : 2;
                if (!double.IsNaN(devA)) { sharpBins[bin] += devA; sharpCnt[bin]++; }
                var nc = Crease(s, rec, gid, i);
                if (nc.sqrMagnitude > 0f) dC.Add(Ang(ni, nc));
                if (Vector3.Dot(ni, na) < 0f) row.flipped++;
                meanImp += ni; meanRec += na;
                for (int c = 0; c < 3; c++)
                {
                    var rd = Norm(s.pos[i] - centers[c]);
                    row.radImp[c] += Ang(ni, rd); row.radRec[c] += Ang(na, rd);
                }
                nRad++;
            }
            row.valid = nRad;
            for (int c = 0; c < 3; c++) { row.radImp[c] /= Math.Max(1, nRad); row.radRec[c] /= Math.Max(1, nRad); }
            row.angW = Stats(dA); row.areaW = Stats(dR); row.crease = Stats(dC);
            row.polyAng = Stats(dPA); row.polyArea = Stats(dPR); row.triUnw = Stats(dTU); row.polyUnw = Stats(dPU);

            // 퍼짐
            var mi = Norm(meanImp); var mr = Norm(meanRec);
            double si = 0, sr = 0;
            foreach (var i in verts) { int g = gid[i]; if (!rec.valid[g]) continue; si += Ang(nrm[i], mi); sr += Ang(rec.angN[g], mr); }
            row.spreadImp = si / Math.Max(1, nRad); row.spreadRec = sr / Math.Max(1, nRad);

            // 인접 변 노멀차
            double ri = 0, rr = 0; int ne = 0;
            foreach (var key in rec.edges)
            {
                int ga = (int)(key / rec.gCount), gb = (int)(key % rec.gCount);
                if (!regionG.Contains(ga) || !regionG.Contains(gb) || !rec.valid[ga] || !rec.valid[gb]) continue;
                ri += Ang(Norm(gImp[ga]), Norm(gImp[gb])); rr += Ang(rec.angN[ga], rec.angN[gb]); ne++;
            }
            row.roughImp = ri / Math.Max(1, ne); row.roughRec = rr / Math.Max(1, ne); row.edgeCount = ne;

            // 같은 위치 다른 노멀 (하드 엣지·분할)
            foreach (var g in regionG)
            {
                row.groups++;
                var mem = rec.gVerts[g];
                if (mem.Count < 2) continue;
                row.multi++;
                double mx = 0;
                for (int a = 0; a < mem.Count; a++) for (int b = a + 1; b < mem.Count; b++) mx = Math.Max(mx, Ang(nrm[mem[a]], nrm[mem[b]]));
                if (mx > 1.0) row.split1++;
                if (mx > 30.0) row.split30++;
            }
            foreach (var i in verts) if (rec.open[gid[i]]) row.open++;

            Rows.Add(row);
            L(string.Format("    [{0} / {1}] 정점 {2} (유효 {3})  임포트↔재계산 평균: 각도가중 {4:F2} / 면적가중 {5:F2} / 꺾임60 {6:F2}",
                row.region, row.subset, row.verts, row.valid, row.angW.mean, row.areaW.mean, row.crease.mean));
            L(string.Format("        각도가중 분포: 중앙 {0:F2}  p90 {1:F2}  최대 {2:F1}  >5° {3:P1}  >15° {4:P1}   (면적가중 p90 {5:F2}, >5° {6:P1})",
                row.angW.median, row.angW.p90, row.angW.max, row.angW.f5, row.angW.f15, row.areaW.p90, row.areaW.f5));
            L(string.Format("        위치 묶음 {0} (2개 이상 {1}, 임포트 노멀 분할 >1° {2}, >30° {3})  열린 경계 정점 {4}  반대 방향 {5}",
                row.groups, row.multi, row.split1, row.split30, row.open, row.flipped));
            L(string.Format("        방사각 임포트/재계산: C1 {0:F2}/{1:F2}  C2 {2:F2}/{3:F2}  C3 {4:F2}/{5:F2}   (재계산-임포트 = C1 {6:+0.00;-0.00} C2 {7:+0.00;-0.00} C3 {8:+0.00;-0.00})",
                row.radImp[0], row.radRec[0], row.radImp[1], row.radRec[1], row.radImp[2], row.radRec[2],
                row.radRec[0] - row.radImp[0], row.radRec[1] - row.radImp[1], row.radRec[2] - row.radImp[2]));
            L(string.Format("        퍼짐 임포트 {0:F2} / 재계산 {1:F2}   인접 변 노멀차 임포트 {2:F3} / 재계산 {3:F3} (변 {4}, 비 {5:F3})",
                row.spreadImp, row.spreadRec, row.roughImp, row.roughRec, ne, row.roughRec > 0 ? row.roughImp / row.roughRec : 0));
            L(string.Format("        다각형(사각형 복원 {0}쌍) 기준: 각도가중 평균 {1:F2} 중앙 {2:F2} p90 {3:F2} >5° {4:P1}  /  면적가중 평균 {5:F2} p90 {6:F2} >5° {7:P1}",
                rec.quads, row.polyAng.mean, row.polyAng.median, row.polyAng.p90, row.polyAng.f5, row.polyArea.mean, row.polyArea.p90, row.polyArea.f5));
            L(string.Format("        무가중(면 노멀 단순 평균): 삼각형 평균 {0:F2} 중앙 {1:F2} p90 {2:F2} 0.1°미만 {7:P1}  /  다각형 평균 {3:F2} 중앙 {4:F2} p90 {5:F2} 0.1°미만 {8:P1}   -> 가장 잘 맞는 계산: {6}",
                row.triUnw.mean, row.triUnw.median, row.triUnw.p90, row.polyUnw.mean, row.polyUnw.median, row.polyUnw.p90, Best(row), row.triUnw.f01, row.polyUnw.f01));
            L(string.Format("        국소 꺾임(닿는 면끼리 최대 각)별 각도가중 편차: <15° {0}개 평균 {1:F2}  15~40° {2}개 평균 {3:F2}  ≥40° {4}개 평균 {5:F2}",
                sharpCnt[0], sharpCnt[0] > 0 ? sharpBins[0] / sharpCnt[0] : 0, sharpCnt[1], sharpCnt[1] > 0 ? sharpBins[1] / sharpCnt[1] : 0, sharpCnt[2], sharpCnt[2] > 0 ? sharpBins[2] / sharpCnt[2] : 0));
            Csv.AppendLine(string.Join(",", new[] {
                row.model, row.mesh, row.region, row.subset, row.verts.ToString(), row.valid.ToString(),
                F(row.angW.mean), F(row.areaW.mean), F(row.crease.mean), F(row.angW.median), F(row.angW.p90), F(row.angW.max), F(row.angW.f5), F(row.angW.f15),
                row.groups.ToString(), row.multi.ToString(), row.split1.ToString(), row.split30.ToString(), row.open.ToString(), row.flipped.ToString(),
                F(row.radImp[0]), F(row.radRec[0]), F(row.radImp[1]), F(row.radRec[1]), F(row.radImp[2]), F(row.radRec[2]),
                F(row.spreadImp), F(row.spreadRec), F(row.roughImp), F(row.roughRec), ne.ToString(), F(row.polyAng.mean), F(row.polyAng.p90), F(row.polyArea.mean) }));
            return row;
        }

        /// <summary>양성 대조: 임포트 노멀을 구 맞춤 중심(C3) 방사 방향으로 SynthMix 만큼 섞은 '편집된 노멀'을 같은 자로 잰다.</summary>
        static void Synthetic(string model, string mesh, string region, Src s, int[] gid, Rec rec, List<int> verts, Vector3[] centers)
        {
            var ed = (Vector3[])s.nrm.Clone();
            foreach (var i in verts) ed[i] = Norm(Vector3.Lerp(s.nrm[i], Norm(s.pos[i] - centers[2]), SynthMix));
            L("    (양성 대조: 같은 영역의 임포트 노멀을 C3 방사 방향으로 " + SynthMix + " 섞은 가짜 편집 노멀)");
            Measure(model, mesh, region, "synthetic", s, ed, gid, rec, verts, centers, "synthetic_radial" + SynthMix.ToString("F1"));
        }

        // ---------------------------------------------------------------- 형상 계산

        static Src MakeSrc(Transform t, Mesh m)
        {
            var l2w = t.localToWorldMatrix;
            var n2w = l2w.inverse.transpose;
            bool flip = l2w.determinant < 0f;
            var v = m.vertices; var n = m.normals;
            var s = new Src { name = m.name, pos = new Vector3[v.Length] };
            var bmin = Vector3.positiveInfinity; var bmax = Vector3.negativeInfinity;
            for (int i = 0; i < v.Length; i++) { s.pos[i] = l2w.MultiplyPoint3x4(v[i]); bmin = Vector3.Min(bmin, s.pos[i]); bmax = Vector3.Max(bmax, s.pos[i]); }
            s.size = v.Length > 0 ? (bmax - bmin).magnitude : 0f;
            if (n != null && n.Length == v.Length)
            {
                s.nrm = new Vector3[n.Length];
                for (int i = 0; i < n.Length; i++) s.nrm[i] = Norm(n2w.MultiplyVector(n[i]));
            }
            var tl = new List<int>(); var ts = new List<int>();
            for (int sub = 0; sub < m.subMeshCount; sub++)
            {
                if (m.GetSubMesh(sub).topology != MeshTopology.Triangles) continue;
                var tri = m.GetTriangles(sub);
                for (int k = 0; k + 2 < tri.Length; k += 3)
                {
                    tl.Add(tri[k]);
                    if (flip) { tl.Add(tri[k + 2]); tl.Add(tri[k + 1]); } else { tl.Add(tri[k + 1]); tl.Add(tri[k + 2]); }
                    ts.Add(sub);
                }
            }
            s.tris = tl.ToArray(); s.triSub = ts.ToArray();
            s.srcOf = new int[v.Length]; s.srcNames = new[] { m.name };
            return s;
        }

        static Src Merge(string name, List<Src> list)
        {
            var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var tris = new List<int>(); var ts = new List<int>(); var so = new List<int>(); var names = new List<string>();
            var bmin = Vector3.positiveInfinity; var bmax = Vector3.negativeInfinity;
            for (int k = 0; k < list.Count; k++)
            {
                int off = pos.Count;
                pos.AddRange(list[k].pos); nrm.AddRange(list[k].nrm);
                foreach (var p in list[k].pos) { bmin = Vector3.Min(bmin, p); bmax = Vector3.Max(bmax, p); }
                foreach (var i in list[k].tris) tris.Add(off + i);
                ts.AddRange(list[k].triSub);
                for (int i = 0; i < list[k].pos.Length; i++) so.Add(k);
                names.Add(list[k].name);
            }
            return new Src { name = name, pos = pos.ToArray(), nrm = nrm.ToArray(), tris = tris.ToArray(), triSub = ts.ToArray(), srcOf = so.ToArray(), srcNames = names.ToArray(), size = (bmax - bmin).magnitude };
        }

        static (long, long, long) Cell(Vector3 p, double inv)
        {
            return ((long)Math.Floor(p.x * inv), (long)Math.Floor(p.y * inv), (long)Math.Floor(p.z * inv));
        }

        /// <summary>허용오차 안의 같은 위치 정점을 하나로 묶는다 (첫 대표점 기준).</summary>
        static int[] Group(Vector3[] pos, float tol, out int count)
        {
            var gid = new int[pos.Length];
            var reps = new List<Vector3>();
            var map = new Dictionary<(long, long, long), List<int>>();
            double inv = 1.0 / Math.Max(tol, 1e-12f);
            float t2 = tol * tol;
            for (int i = 0; i < pos.Length; i++)
            {
                var p = pos[i]; var c = Cell(p, inv);
                int found = -1;
                for (int dx = -1; dx <= 1 && found < 0; dx++)
                    for (int dy = -1; dy <= 1 && found < 0; dy++)
                        for (int dz = -1; dz <= 1 && found < 0; dz++)
                            if (map.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var lst))
                                foreach (var g in lst) if ((reps[g] - p).sqrMagnitude <= t2) { found = g; break; }
                if (found < 0)
                {
                    found = reps.Count; reps.Add(p);
                    if (!map.TryGetValue(c, out var l2)) { l2 = new List<int>(); map[c] = l2; }
                    l2.Add(found);
                }
                gid[i] = found;
            }
            count = reps.Count;
            return gid;
        }

        static Rec Recompute(Src s, int[] gid, int gCount)
        {
            var r = new Rec { gCount = gCount };
            int nT = s.tris.Length / 3;
            var angSum = new Vector3[gCount]; var angW = new double[gCount]; var areaSum = new Vector3[gCount]; var unwSum = new Vector3[gCount];
            r.fn = new Vector3[nT]; r.cornerAng = new float[nT * 3];
            r.gVerts = new List<int>[gCount]; r.gCorners = new List<int>[gCount];
            for (int g = 0; g < gCount; g++) { r.gVerts[g] = new List<int>(2); r.gCorners[g] = new List<int>(6); }
            for (int i = 0; i < s.pos.Length; i++) r.gVerts[gid[i]].Add(i);
            r.vertTris = new List<int>[s.pos.Length];
            var edgeCnt = new Dictionary<long, int>();
            for (int t = 0; t < nT; t++)
            {
                int a = s.tris[3 * t], b = s.tris[3 * t + 1], c = s.tris[3 * t + 2];
                int ga = gid[a], gb = gid[b], gc = gid[c];
                if (ga == gb || gb == gc || ga == gc) { r.degenerate++; continue; }
                Vector3 pa = s.pos[a], pb = s.pos[b], pc = s.pos[c];
                Vector3 cr = Vector3.Cross(pb - pa, pc - pa);
                double len = Math.Sqrt((double)cr.x * cr.x + (double)cr.y * cr.y + (double)cr.z * cr.z);
                if (!(len > 0)) { r.degenerate++; continue; }
                Vector3 n = new Vector3((float)(cr.x / len), (float)(cr.y / len), (float)(cr.z / len));
                r.fn[t] = n;
                float aA = (float)(Ang(pb - pa, pc - pa) * Math.PI / 180.0);
                float aB = (float)(Ang(pa - pb, pc - pb) * Math.PI / 180.0);
                float aC = (float)(Ang(pa - pc, pb - pc) * Math.PI / 180.0);
                if (float.IsNaN(aA) || float.IsNaN(aB) || float.IsNaN(aC)) { r.degenerate++; r.fn[t] = Vector3.zero; continue; }
                r.cornerAng[3 * t] = aA; r.cornerAng[3 * t + 1] = aB; r.cornerAng[3 * t + 2] = aC;
                angSum[ga] += n * aA; angSum[gb] += n * aB; angSum[gc] += n * aC; unwSum[ga] += n; unwSum[gb] += n; unwSum[gc] += n;
                angW[ga] += aA; angW[gb] += aB; angW[gc] += aC;
                areaSum[ga] += cr; areaSum[gb] += cr; areaSum[gc] += cr;
                r.gCorners[ga].Add(3 * t); r.gCorners[gb].Add(3 * t + 1); r.gCorners[gc].Add(3 * t + 2);
                AddVT(r, a, t); AddVT(r, b, t); AddVT(r, c, t);
                AddEdge(edgeCnt, ga, gb, gCount); AddEdge(edgeCnt, gb, gc, gCount); AddEdge(edgeCnt, gc, ga, gCount);
            }
            r.open = new bool[gCount];
            foreach (var kv in edgeCnt)
            {
                r.edges.Add(kv.Key);
                if (kv.Value == 1) { r.open[(int)(kv.Key / gCount)] = true; r.open[(int)(kv.Key % gCount)] = true; }
            }
            r.angN = new Vector3[gCount]; r.areaN = new Vector3[gCount]; r.valid = new bool[gCount];
            for (int g = 0; g < gCount; g++)
            {
                r.angN[g] = Norm(angSum[g]); r.areaN[g] = Norm(areaSum[g]);
                double mag = Math.Sqrt((double)angSum[g].x * angSum[g].x + (double)angSum[g].y * angSum[g].y + (double)angSum[g].z * angSum[g].z);
                r.valid[g] = angW[g] > 0 && mag / angW[g] > 0.2;
                if (angW[g] > 0 && !r.valid[g]) r.cancelled++;
            }

            // 다각형 기준 변형
            var pAng = new Vector3[gCount]; var pArea = new Vector3[gCount]; var pUnw = new Vector3[gCount];
            for (int t = 0; t < nT; t++)
            {
                if (r.fn[t].sqrMagnitude == 0f) continue;
                int[] poly = null;
                bool quad =t + 1 < nT && r.fn[t + 1].sqrMagnitude > 0f && s.triSub[t] == s.triSub[t + 1] && TryQuad(s.tris, t, out poly);
                if (!quad) poly = new[] { s.tris[3 * t], s.tris[3 * t + 1], s.tris[3 * t + 2] };
                else { r.quads++; }
                var p0 = s.pos[poly[0]]; var nw = Vector3.zero;
                for (int k = 1; k + 1 < poly.Length; k++) nw += Vector3.Cross(s.pos[poly[k]] - p0, s.pos[poly[k + 1]] - p0);
                var nn = Norm(nw);
                if (nn.sqrMagnitude > 0f)
                {
                    for (int k = 0; k < poly.Length; k++)
                    {
                        var pc = s.pos[poly[k]]; var pp = s.pos[poly[(k + poly.Length - 1) % poly.Length]]; var pn = s.pos[poly[(k + 1) % poly.Length]];
                        double a = Ang(pn - pc, pp - pc); if (double.IsNaN(a)) continue;
                        int g = gid[poly[k]];
                        pAng[g] += nn * (float)(a * Math.PI / 180.0); pArea[g] += nw; pUnw[g] += nn;
                    }
                }
                if (quad) t++;
            }
            r.polyAngN = new Vector3[gCount]; r.polyAreaN = new Vector3[gCount]; r.triUnwN = new Vector3[gCount]; r.polyUnwN = new Vector3[gCount];
            for (int g = 0; g < gCount; g++) { r.polyAngN[g] = Norm(pAng[g]); r.polyAreaN[g] = Norm(pArea[g]); r.triUnwN[g] = Norm(unwSum[g]); r.polyUnwN[g] = Norm(pUnw[g]); }
            return r;
        }

        /// <summary>삼각형 t, t+1 이 반대 방향으로 변 하나를 공유하면 사각형 꼭짓점 순서를 돌려준다.</summary>
        static bool TryQuad(int[] tris, int t, out int[] quad)
        {
            quad = null;
            int a = tris[3 * t], b = tris[3 * t + 1], c = tris[3 * t + 2];
            int d = tris[3 * t + 3], e = tris[3 * t + 4], f = tris[3 * t + 5];
            int[] t1 = { a, b, c }; int[] t2 = { d, e, f };
            for (int k = 0; k < 3; k++)
            {
                int u = t1[k], v = t1[(k + 1) % 3], w = t1[(k + 2) % 3];   // 공유 후보 변 u→v
                for (int m = 0; m < 3; m++)
                {
                    if (t2[m] == v && t2[(m + 1) % 3] == u)
                    {
                        int x = t2[(m + 2) % 3];
                        if (x == w || x == u || x == v) return false;
                        quad = new[] { v, w, u, x };   // v→w→u 다음 공유 변 u→v 자리에 x 를 끼움
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>그 위치에 닿는 면 노멀끼리의 최대 각 (국소 꺾임 정도).</summary>
        static double Sharp(Rec rec, int g)
        {
            var cs = rec.gCorners[g]; double mx = 0;
            for (int a = 0; a < cs.Count; a++) for (int b = a + 1; b < cs.Count; b++) mx = Math.Max(mx, Ang(rec.fn[cs[a] / 3], rec.fn[cs[b] / 3]));
            return mx;
        }

        static void AddVT(Rec r, int v, int t) { if (r.vertTris[v] == null) r.vertTris[v] = new List<int>(4); r.vertTris[v].Add(t); }

        static void AddEdge(Dictionary<long, int> d, int a, int b, int n)
        {
            long key = a < b ? (long)a * n + b : (long)b * n + a;
            d[key] = (d.TryGetValue(key, out var c) ? c : 0) + 1;
        }

        /// <summary>꺾임 60°: 이 정점이 직접 속한 면들의 평균 방향과 60° 안쪽인 면만 각도 가중 평균.</summary>
        static Vector3 Crease(Src s, Rec rec, int[] gid, int i)
        {
            var vt = rec.vertTris[i];
            if (vt == null) return Vector3.zero;
            var refN = Vector3.zero; foreach (var t in vt) refN += rec.fn[t];
            refN = Norm(refN);
            if (refN.sqrMagnitude == 0f) return Vector3.zero;
            var sum = Vector3.zero;
            foreach (var corner in rec.gCorners[gid[i]])
            {
                int t = corner / 3;
                if (Vector3.Dot(rec.fn[t], refN) >= 0.5f) sum += rec.fn[t] * rec.cornerAng[corner];
            }
            return Norm(sum);
        }

        static Vector3[] Centers(Src s, List<int> verts, Vector3[] nrm)
        {
            var c = new Vector3[3];
            if (verts.Count == 0) return c;
            double sx = 0, sy = 0, sz = 0;
            var bmin = Vector3.positiveInfinity; var bmax = Vector3.negativeInfinity; var mn = Vector3.zero;
            foreach (var i in verts) { var p = s.pos[i]; sx += p.x; sy += p.y; sz += p.z; bmin = Vector3.Min(bmin, p); bmax = Vector3.Max(bmax, p); mn += nrm[i]; }
            c[0] = new Vector3((float)(sx / verts.Count), (float)(sy / verts.Count), (float)(sz / verts.Count));
            float fwd = mn.z >= 0f ? 1f : -1f;
            var bc = (bmin + bmax) * 0.5f;
            c[1] = bc - new Vector3(0f, 0f, fwd * (bmax.z - bmin.z) * 0.5f);
            c[2] = SphereFit(s, verts, c[0]);
            var mnN = Norm(mn);
            L(string.Format("        영역 경계상자 {0} ~ {1}, 평균 임포트 노멀 {2} (앞={3}Z)  C1 {4}  C2 {5}  C3 {6} (C3 반지름 {7:F4})",
                bmin.ToString("F4"), bmax.ToString("F4"), mnN.ToString("F3"), fwd > 0 ? "+" : "-", c[0].ToString("F4"), c[1].ToString("F4"), c[2].ToString("F4"), MeanDist(s, verts, c[2])));
            return c;
        }

        static float MeanDist(Src s, List<int> verts, Vector3 c)
        {
            double d = 0; foreach (var i in verts) d += (s.pos[i] - c).magnitude; return (float)(d / Math.Max(1, verts.Count));
        }

        /// <summary>대수적 최소제곱 구 맞춤: |q|² = 2a·q + d.</summary>
        static Vector3 SphereFit(Src s, List<int> verts, Vector3 mean)
        {
            var A = new double[4, 5];
            foreach (var i in verts)
            {
                var q = s.pos[i] - mean;
                double[] row = { 2.0 * q.x, 2.0 * q.y, 2.0 * q.z, 1.0 };
                double rhs = (double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z;
                for (int r = 0; r < 4; r++) { for (int c = 0; c < 4; c++) A[r, c] += row[r] * row[c]; A[r, 4] += row[r] * rhs; }
            }
            for (int col = 0; col < 4; col++)
            {
                int piv = col; for (int r = col + 1; r < 4; r++) if (Math.Abs(A[r, col]) > Math.Abs(A[piv, col])) piv = r;
                if (Math.Abs(A[piv, col]) < 1e-30) return mean;
                for (int c = 0; c < 5; c++) { var tmp = A[col, c]; A[col, c] = A[piv, c]; A[piv, c] = tmp; }
                for (int r = 0; r < 4; r++)
                {
                    if (r == col) continue;
                    double f = A[r, col] / A[col, col];
                    for (int c = col; c < 5; c++) A[r, c] -= f * A[col, c];
                }
            }
            return mean + new Vector3((float)(A[0, 4] / A[0, 0]), (float)(A[1, 4] / A[1, 1]), (float)(A[2, 4] / A[2, 2]));
        }

        static List<int> FrontHalf(Src s, List<int> verts)
        {
            var bmin = Vector3.positiveInfinity; var bmax = Vector3.negativeInfinity; var mn = Vector3.zero;
            foreach (var i in verts) { bmin = Vector3.Min(bmin, s.pos[i]); bmax = Vector3.Max(bmax, s.pos[i]); mn += s.nrm[i]; }
            float cz = (bmin.z + bmax.z) * 0.5f; float fwd = mn.z >= 0f ? 1f : -1f;
            var outL = new List<int>();
            foreach (var i in verts) if ((s.pos[i].z - cz) * fwd > 0f) outL.Add(i);
            return outL;
        }

        // ---------------------------------------------------------------- 보조

        static Vector3 Norm(Vector3 v)
        {
            double l = Math.Sqrt((double)v.x * v.x + (double)v.y * v.y + (double)v.z * v.z);
            if (!(l > 0)) return Vector3.zero;
            return new Vector3((float)(v.x / l), (float)(v.y / l), (float)(v.z / l));
        }

        /// <summary>두 벡터 사이 각(도). atan2(|a×b|, a·b) 로 작은 각에서도 정확하게.</summary>
        static double Ang(Vector3 a, Vector3 b)
        {
            double ax = a.x, ay = a.y, az = a.z, bx = b.x, by = b.y, bz = b.z;
            double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
            double cr = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            double d = ax * bx + ay * by + az * bz;
            if (cr == 0 && d == 0) return double.NaN;
            return Math.Atan2(cr, d) * 180.0 / Math.PI;
        }

        static St Stats(List<double> a)
        {
            var s = new St();
            a.RemoveAll(double.IsNaN);
            s.n = a.Count; if (a.Count == 0) return s;
            a.Sort();
            double sum = 0; int o5 = 0, o15 = 0;
            int o01 = 0; foreach (var x in a) { sum += x; if (x > 5) o5++; if (x > 15) o15++; if (x < 0.1) o01++; }
            s.mean = sum / a.Count; s.median = a[a.Count / 2]; s.p90 = a[(int)(0.9 * (a.Count - 1))]; s.max = a[a.Count - 1];
            s.f5 = (double)o5 / a.Count; s.f15 = (double)o15 / a.Count; s.f01 = (double)o01 / a.Count;
            return s;
        }

        static string F(double x) { return x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture); }

        static string HPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Insert(0, x.name);
            return string.Join("/", parts.ToArray());
        }

        static void LogImporter(string path)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null) { L("  임포터 없음"); return; }
            L(string.Format("  임포트 설정(읽기만): normals={0} calcMode={1} smoothAngle={2} smoothSrc={3} tangents={4} weld={5} keepQuads={6} optimize={7} blendShapes={8} readable={9} scale={10}/{11}",
                mi.importNormals, mi.normalCalculationMode, mi.normalSmoothingAngle, mi.normalSmoothingSource, mi.importTangents, mi.weldVertices, mi.keepQuads,
                mi.meshOptimizationFlags, mi.importBlendShapes, mi.isReadable, mi.globalScale, mi.useFileScale));
            L("  userData: " + (string.IsNullOrEmpty(mi.userData) ? "(없음)" : mi.userData));
            try
            {
                var bytes = File.ReadAllBytes(path);
                L(string.Format("  FBX 파일 {0:N0} 바이트, 'LayerElementNormal' {1}회, 'LayerElementSmoothing' {2}회, 'LayerElementTangent' {3}회",
                    bytes.Length, Count(bytes, "LayerElementNormal"), Count(bytes, "LayerElementSmoothing"), Count(bytes, "LayerElementTangent")));
            }
            catch (Exception e) { L("  FBX 읽기 실패: " + e.Message); }
        }

        static int Count(byte[] hay, string needle)
        {
            var n = Encoding.ASCII.GetBytes(needle); int c = 0;
            for (int i = 0; i + n.Length <= hay.Length; i++)
            {
                if (hay[i] != n[0]) continue;
                int j = 1; while (j < n.Length && hay[i + j] == n[j]) j++;
                if (j == n.Length) { c++; i += n.Length - 1; }
            }
            return c;
        }

        static string Best(Row r)
        {
            var c = new[] { ("삼각형 각도가중", r.angW), ("삼각형 면적가중", r.areaW), ("꺾임60", r.crease), ("다각형 각도가중", r.polyAng),
                            ("다각형 면적가중", r.polyArea), ("삼각형 무가중", r.triUnw), ("다각형 무가중", r.polyUnw) };
            var best = c[0];
            foreach (var x in c) if (x.Item2.n > 0 && (best.Item2.n == 0 || x.Item2.mean < best.Item2.mean)) best = x;
            return string.Format("{0} 평균 {1:F2}° (0.1° 미만 일치 {2:P1})", best.Item1, best.Item2.mean, best.Item2.f01);
        }

        static void Summary()
        {
            L(""); L("==================== 요약 (각도가중 재계산 대비, 'all' 과 'interior')");
            L("모델 | 메시 | 영역 | 부분 | 정점 | 평균(각) | 평균(면적) | 꺾임60 | 다각형(각) | 다각형(면적) | 중앙 | p90 | >5° | >15° | 분할>1° | 방사 임포트/재계산 C1 C2 C3 | 변 노멀차 비");
            foreach (var r in Rows)
            {
                L(string.Format("{0} | {1} | {2} | {3} | {4} | {5:F2} | {6:F2} | {7:F2} | {19:F2} | {20:F2} | {21:F2} | {8:F2} | {9:P1} | {10:P1} | {11} | {12:F1}/{13:F1} {14:F1}/{15:F1} {16:F1}/{17:F1} | {18:F3}",
                    r.model, r.mesh, r.region, r.subset, r.verts, r.angW.mean, r.areaW.mean, r.crease.mean, r.angW.p90, r.angW.f5, r.angW.f15, r.split1,
                    r.radImp[0], r.radRec[0], r.radImp[1], r.radRec[1], r.radImp[2], r.radRec[2], r.roughRec > 0 ? r.roughImp / r.roughRec : 0,
                    r.polyAng.mean, r.polyArea.mean, r.angW.median) + " | 최적 " + Best(r));
            }
        }
    }
}
