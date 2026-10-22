// FBX2NPRRelightPortfolio.cs — 포트폴리오 그림 두 장(4체 노멀 정리 클레이 비교, 블렌더 면 반전 구멍)을 최종 얼굴 명암(지수함수 꼴 명암선)으로 다시 찍는다.
// 프리팹·에셋은 바꾸지 않는다(임시 메시·재질만 만들고 지운다). 결과 NormalTestOut/relight/.
// 배치: -executeMethod FBX2NPR.EditorTools.FBX2NPRRelightPortfolio.Probe | .ClaySheet | .ColorSheet | .Winding
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRRelightPortfolio
    {
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "relight"); } }
        static readonly List<string> Log = new List<string>();
        static void L(string s) { Log.Add(s); Debug.Log("[Relight] " + s); }
        static void Flush(string name) { Directory.CreateDirectory(OutDir); File.WriteAllText(Path.Combine(OutDir, name), string.Join("\n", Log.ToArray()), new UTF8Encoding(true)); Log.Clear(); }

        static string FindModel(string name)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer; if (smr != null) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>(); return mf != null ? mf.sharedMesh : null;
        }
        static void SetMesh(Renderer r, Mesh m)
        {
            var smr = r as SkinnedMeshRenderer; if (smr != null) { smr.sharedMesh = m; return; }
            var mf = r.GetComponent<MeshFilter>(); if (mf != null) mf.sharedMesh = m;
        }

        // 재질을 임시 사본으로 바꾸고 색을 끈다 (클레이). 툰 셰이더는 알베도 섞기만 0 으로 해서 머리카락 알파 컷아웃은 그대로 둔다.
        // clay=false 면 색은 그대로 두고 사본만 만든다 (리그가 값을 써 넣어도 원본 재질 에셋이 바뀌지 않게).
        static void Clay(Renderer r, List<UnityEngine.Object> temps, Dictionary<Material, Material> map, bool clay = true)
        {
            var orig = r.sharedMaterials; var copies = new Material[orig.Length];
            for (int i = 0; i < orig.Length; i++)
            {
                if (orig[i] == null) continue;
                Material m;
                if (!map.TryGetValue(orig[i], out m))
                {
                    m = new Material(orig[i]); temps.Add(m); map[orig[i]] = m;
                    if (!clay) { }
                    else if (m.HasProperty("_AlbedoOn")) m.SetFloat("_AlbedoOn", 0f);
                    else
                    {
                        bool clip = (m.HasProperty("_AlphaClip") && m.GetFloat("_AlphaClip") > 0.5f) || m.IsKeywordEnabled("_ALPHATEST_ON");
                        if (!clip) foreach (var t in new[] { "_BaseMap", "_MainTex" }) if (m.HasProperty(t)) m.SetTexture(t, Texture2D.whiteTexture);
                        foreach (var c in new[] { "_BaseColor", "_Color" }) if (m.HasProperty(c)) { var col = m.GetColor(c); m.SetColor(c, new Color(1f, 1f, 1f, col.a)); }
                    }
                    L("  " + (clay ? "클레이 " : "색 유지 사본 ") + r.name + " " + orig[i].name + " (" + orig[i].shader.name + ")");
                }
                copies[i] = m;
            }
            r.sharedMaterials = copies;
        }

        // 4체 × (원본 노멀 / 노멀 정리 / 정리 + 외곽선), 최종 툰 프리팹을 흰 텍스처(클레이)로. 원본 노멀은 변환기가 UV3 에 보존한 값을 임시 메시에 넣어 찍는다.
        public static void ClaySheet() { Sheet(true, new[] { "Ch07", "TheBoss", "Jones", "unitychan" }, new[] { "orignormal", "cleaned", "cleaned_outline" }); }

        // 같은 장면을 색 그대로 — 최종 툰 프리팹에서 얼굴 노멀만 원본으로 바꾼 칸을 더한다 (10/19 발표 자료). 캐릭터는 FBX2NPR_RELIGHT_CHARS(쉼표, 기본 unitychan).
        public static void ColorSheet()
        {
            var env = Environment.GetEnvironmentVariable("FBX2NPR_RELIGHT_CHARS");
            var chars = string.IsNullOrEmpty(env) ? new[] { "unitychan" } : env.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            Sheet(false, chars, new[] { "orignormal", "cleaned", "cleaned_outline", "orignormal_outline" });
        }

        static void Sheet(bool clay, string[] chars, string[] names)
        {
            string tag = clay ? "clay" : "color";
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            var temps = new List<UnityEngine.Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                Directory.CreateDirectory(OutDir);
                foreach (var c in chars)
                {
                    FBX2NPRRuntimeCheck.NewStage(false);
                    var crit = FBX2NPRRuntimeCheck.Crit(c);
                    var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FindModel(c)));
                    Bounds head; int hv; if (!FBX2NPRParseBake.HeadBounds(pbr, crit, out head, out hv)) head = FBX2NPRRuntimeCheck.HeadOf(pbr, c, new List<string>());
                    UnityEngine.Object.DestroyImmediate(pbr);
                    var toon = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath(c + "_Toon")));
                    var trig = toon.GetComponentInChildren<ToonFaceRig>(true);
                    var map = new Dictionary<Material, Material>();
                    foreach (var r in toon.GetComponentsInChildren<Renderer>(true)) Clay(r, temps, map, clay);
                    // 리그가 재질에 직접 넣는 목록도 사본으로 (원본 재질 에셋을 건드리지 않게)
                    Func<List<Material>, List<Material>> remap = list => { var o = new List<Material>(); foreach (var m in list) { Material mm; o.Add(m != null && map.TryGetValue(m, out mm) ? mm : m); } return o; };
                    trig.faceMaterials = remap(trig.faceMaterials); trig.otherMaterials = remap(trig.otherMaterials);
                    // 원본 노멀 임시 메시
                    var swaps = new List<KeyValuePair<Renderer, Mesh[]>>();
                    foreach (var r in trig.faceRenderers)
                    {
                        if (r == null) continue; var m = MeshOf(r); if (m == null) continue;
                        var uv3 = new List<Vector3>(); m.GetUVs(3, uv3);
                        int nz = 0; foreach (var v in uv3) if (v.sqrMagnitude > 1e-8f) nz++;
                        if (uv3.Count != m.vertexCount || nz == 0) { L(c + " " + r.name + ": UV3 원본 노멀 없음(" + uv3.Count + "/" + m.vertexCount + ") — 원본 칸은 같은 메시"); continue; }
                        var tmp = UnityEngine.Object.Instantiate(m); temps.Add(tmp);
                        var n = new Vector3[uv3.Count]; for (int i = 0; i < n.Length; i++) n[i] = uv3[i].sqrMagnitude > 1e-8f ? uv3[i].normalized : m.normals[i];
                        tmp.normals = n;
                        swaps.Add(new KeyValuePair<Renderer, Mesh[]>(r, new[] { m, tmp }));
                        // 정리된 노멀과 원본 노멀의 각도 차이 (얼굴 가중치 UV2.x > 0.5 인 정점만)
                        var cur = m.normals; var fw = new List<Vector2>(); m.GetUVs(2, fw);
                        double sum = 0; int cnt = 0, over20 = 0;
                        for (int i = 0; i < n.Length; i++) { if (fw.Count == n.Length && fw[i].x < 0.5f) continue; float ang = Vector3.Angle(cur[i], n[i]); sum += ang; cnt++; if (ang > 20f) over20++; }
                        var tg = new List<Vector4>(); m.GetTangents(tg);
                        double tsum = 0; for (int i = 0; i < Math.Min(tg.Count, n.Length); i++) tsum += Vector3.Angle((Vector3)tg[i], n[i]);
                        L(c + " " + r.name + ": 원본 노멀 " + nz + "/" + uv3.Count + " 정점, 얼굴 정점 " + cnt + " 평균 각도차 " + (sum / Math.Max(1, cnt)).ToString("F1") + "°, 20° 넘음 " + over20 + ", 탄젠트-UV3 평균 " + (tsum / Math.Max(1, n.Length)).ToString("F1") + "°");
                    }
                    for (int k = 0; k < names.Length; k++)
                    {
                        bool useOrig = names[k].StartsWith("orignormal"), outline = names[k].EndsWith("_outline");
                        foreach (var s in swaps) SetMesh(s.Key, useOrig ? s.Value[1] : s.Value[0]);
                        // FBX2NPRFinalSheet 와 같은 순서: 외곽선 설정 → 껐다 켜기 → Apply → 선 두께 ×2 (SS2)
                        float baseThick = 1f;
                        if (feat != null)
                        {
                            if (outline) { trig.ApplyOutline(); baseThick = trig.outlineThickness; }
                            else feat.SetActive(false);
                        }
                        toon.SetActive(false); toon.SetActive(true); trig.Apply();
                        if (feat != null && !outline) feat.SetActive(false);   // 껐다 켤 때 리그 OnEnable 이 ApplyOutline 으로 피처를 다시 켠다
                        foreach (var s in swaps) if (MeshOf(s.Key) != (useOrig ? s.Value[1] : s.Value[0])) L(c + " 경고: " + s.Key.name + " 메시 교체가 풀림 (" + MeshOf(s.Key).name + ")");
                        if (feat != null && outline) feat.settings.thickness = baseThick * 2f;
                        FBX2NPRCompare.SetSS(2);
                        FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + "_" + tag + "_" + names[k] + ".png"), Vector3.forward, false, 1.18f);
                        if (feat != null && outline) feat.settings.thickness = baseThick;
                        L(c + " " + tag + " " + names[k] + " 외곽선 피처 " + (feat != null && feat.isActive) + " outline=" + trig.outline + " drive=" + trig.driveOutlineSettings);
                    }
                    foreach (var s in swaps) SetMesh(s.Key, s.Value[0]);
                    UnityEngine.Object.DestroyImmediate(toon);
                    L(c + " 찍음");
                }
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                Flush("log_" + tag + ".txt"); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        // 블렌더 면 반전 실험 머리(Head_Spherized = 정상, Head_BadWinding = 면 10.5% 반전)를 최종 얼굴 명암 설정으로 다시 찍는다.
        // 머리 메시는 블렌더에서 원점으로 옮겨져 있어, Ch07 툰 리그의 값을 복사하고 코끝·얼굴 중심만 이 메시에서 다시 잡는다.
        public static void Winding()
        {
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            var temps = new List<UnityEngine.Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                Directory.CreateDirectory(OutDir);
                FBX2NPRRuntimeCheck.NewStage(false);
                var src = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath("Ch07_Toon")));
                var srcRig = src.GetComponentInChildren<ToonFaceRig>(true);
                var faceMat = srcRig.faceRenderers[0].sharedMaterials[0];
                L("원본 얼굴 재질 " + faceMat.name + " (" + faceMat.shader.name + ")");
                var made = new List<GameObject>();
                foreach (var pair in new[] { new[] { "normal", "Assets/NormalTest/Head_Spherized.fbx" }, new[] { "bad", "Assets/NormalTest/Head_BadWinding.fbx" } })
                {
                    // 9월 실험(NormalTestRunner.BuildRig)과 같이 메시를 단위 변환으로 놓는다. FBX 프리팹 그대로면 회전(270,0,0) 때문에 얼굴이 위를 본다.
                    Mesh mesh = null;
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(pair[1])) if (o is Mesh) { mesh = (Mesh)o; break; }
                    var go = new GameObject("Head_" + pair[0]);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>(); r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                    var mat = new Material(faceMat); temps.Add(mat);
                    if (mat.HasProperty("_Albedo")) mat.SetTexture("_Albedo", Texture2D.whiteTexture);
                    if (mat.HasProperty("_FaceWeightOn")) mat.SetFloat("_FaceWeightOn", 0f);
                    r.sharedMaterial = mat;
                    var b = r.bounds; var l2w = r.localToWorldMatrix; var vs = mesh.vertices;
                    Vector3 tip = b.center; float best = float.NegativeInfinity;
                    foreach (var v in vs) { var w = l2w.MultiplyPoint3x4(v); if (Mathf.Abs(w.x - b.center.x) < b.size.x * 0.08f && w.z > best) { best = w.z; tip = w; } }
                    var rigGo = new GameObject("RelightRig_" + pair[0]); made.Add(rigGo);
                    var tipT = new GameObject("noseTip").transform; tipT.SetParent(rigGo.transform, false); tipT.position = tip;
                    var cT = new GameObject("faceCenter").transform; cT.SetParent(rigGo.transform, false); cT.position = b.center;
                    var rig = rigGo.AddComponent<ToonFaceRig>();
                    EditorUtility.CopySerialized(srcRig, rig);
                    rig.faceRenderers = new List<Renderer> { r }; rig.otherRenderers = new List<Renderer>();
                    rig.faceMaterials = new List<Material>(); rig.otherMaterials = new List<Material>();
                    rig.outlineFeature = null; rig.driveOutlineSettings = false;
                    rig.noseTip = tipT; rig.faceCenter = cT; rig.noseShadow = false; rig.avoidEyes = false; rig.eyeA = null; rig.eyeB = null;
                    rig.generatedAssets = new List<string>();
                    made.Add(go);
                    L(pair[0] + ": bounds " + b.center.ToString("F4") + " size " + b.size.ToString("F4") + " tip " + tip.ToString("F4") + " verts " + mesh.vertexCount + " tris " + (mesh.triangles.Length / 3) + " chinScale " + rig.chinScale);
                }
                src.SetActive(false);
                if (feat != null) feat.SetActive(false);
                // 같은 셰이더에서 Cull Off 만 Cull Back 으로 바꾼 사본 (메모리 안에서만). 툰 셰이더는 블렌더와 맞추려 양면 렌더라, 후면 컬링 상황을 이 사본으로 재현한다.
                var shPath = AssetDatabase.GetAssetPath(faceMat.shader);
                var text = File.ReadAllText(Path.Combine(Directory.GetParent(Application.dataPath).FullName, shPath));
                int nCull = 0; text = System.Text.RegularExpressions.Regex.Replace(text, @"Cull\s+Off", m => { nCull++; return "Cull Back"; });
                text = new System.Text.RegularExpressions.Regex("Shader\\s+\"([^\"]+)\"").Replace(text, m => "Shader \"Hidden/" + m.Groups[1].Value + "_CullBack\"", 1);
                var cullSh = ShaderUtil.CreateShaderAsset(text, true); temps.Add(cullSh);
                L("후면 컬링 사본 " + cullSh.name + " (Cull Off → Back " + nCull + "곳, 오류 " + ShaderUtil.ShaderHasError(cullSh) + ")");
                var heads = new Dictionary<string, GameObject>(); var rigs = new Dictionary<string, ToonFaceRig>();
                foreach (var g in made) { var fr = g.GetComponent<ToonFaceRig>(); if (fr != null) rigs[g.name.Substring("RelightRig_".Length)] = fr; }
                foreach (var kv in rigs) foreach (var g in made) if (g.GetComponent<ToonFaceRig>() == null && g.GetComponentInChildren<Renderer>(true) == kv.Value.faceRenderers[0]) heads[kv.Key] = g;
                var shots = new[] { new[] { "normal", "normal", "off" }, new[] { "bad_cullback", "bad", "back" }, new[] { "bad_culloff", "bad", "off" }, new[] { "normal_cullback", "normal", "back" } };
                Bounds frame = rigs["normal"].faceRenderers[0].bounds;
                foreach (var s in shots)
                {
                    foreach (var h in heads.Values) h.SetActive(false);
                    heads[s[1]].SetActive(true);
                    var rig = rigs[s[1]]; var r = rig.faceRenderers[0];
                    var m0 = r.sharedMaterial;
                    if (s[2] == "back") { var mb = new Material(m0); temps.Add(mb); mb.shader = cullSh; mb.CopyPropertiesFromMaterial(m0); r.sharedMaterial = mb; }
                    rig.Apply();
                    FBX2NPRCompare.SetSS(2);
                    FBX2NPRCompare.Shoot(frame, 700, 700, Path.Combine(OutDir, "winding_" + s[0] + ".png"), Vector3.forward, false, 1.12f);
                    FBX2NPRCompare.Shoot(frame, 400, 400, Path.Combine(OutDir, "winding_" + s[0] + "_side.png"), Vector3.right, false, 1.12f);
                    r.sharedMaterial = m0;
                    L("찍음 " + s[0]);
                }
                // 머리 화소 중 사라진 화소 (배경과 색 차이로 판정)
                Func<string, Color32[]> load = n => { var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(Path.Combine(OutDir, "winding_" + n + ".png"))); var px = t.GetPixels32(); UnityEngine.Object.DestroyImmediate(t); return px; };
                var bg = new Color32(51, 51, 56, 255);
                Func<Color32, bool> isBg = c => Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b) < 12;
                var a = load("normal");
                foreach (var n in new[] { "bad_cullback", "bad_culloff", "normal_cullback" })
                {
                    var bpx = load(n); int head = 0, lost = 0, changed = 0;
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (isBg(a[i])) continue; head++;
                        if (isBg(bpx[i])) lost++;
                        else if (Mathf.Abs(a[i].r - bpx[i].r) + Mathf.Abs(a[i].g - bpx[i].g) + Mathf.Abs(a[i].b - bpx[i].b) > 6) changed++;
                    }
                    L("대조 normal vs " + n + ": 머리 화소 " + head + ", 사라짐 " + lost + " (" + (100.0 * lost / Math.Max(1, head)).ToString("F2") + "%), 색 바뀜 " + changed);
                }
                foreach (var g in made) UnityEngine.Object.DestroyImmediate(g);
                UnityEngine.Object.DestroyImmediate(src);
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                if (feat != null) feat.SetActive(featWas);
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                Flush("log_winding.txt"); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        // 실험 머리 메시와 최종 Ch07 툰 프리팹의 위치·크기를 비교한다.
        public static void Probe()
        {
            try
            {
                FBX2NPRRuntimeCheck.NewStage(false);
                foreach (var p in new[] { "Assets/NormalTest/Head_Spherized.fbx", "Assets/NormalTest/Head_BadWinding.fbx", "Assets/NormalTest/Head_Original.fbx" })
                {
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(p));
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        var mf = r.GetComponent<MeshFilter>(); var m = mf != null ? mf.sharedMesh : (r is SkinnedMeshRenderer ? ((SkinnedMeshRenderer)r).sharedMesh : null);
                        L(p + " | " + r.name + " bounds " + r.bounds.center.ToString("F4") + " size " + r.bounds.size.ToString("F4") + " verts " + (m != null ? m.vertexCount : -1) + " tris " + (m != null ? m.triangles.Length / 3 : -1) + " mat " + (r.sharedMaterial != null ? r.sharedMaterial.shader.name : "-") + " lossyScale " + r.transform.lossyScale.ToString("F4") + " rot " + r.transform.rotation.eulerAngles.ToString("F1"));
                    }
                    UnityEngine.Object.DestroyImmediate(go);
                }
                var toon = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath("Ch07_Toon")));
                var rig = toon.GetComponentInChildren<ToonFaceRig>(true);
                toon.SetActive(false); toon.SetActive(true); rig.Apply();
                foreach (var r in rig.faceRenderers) if (r != null) L("Ch07_Toon face " + r.name + " bounds " + r.bounds.center.ToString("F4") + " size " + r.bounds.size.ToString("F4") + " mats " + r.sharedMaterials.Length);
                L("rig noseTip " + (rig.noseTip != null ? rig.noseTip.position.ToString("F4") : "-") + " faceCenter " + (rig.faceCenter != null ? rig.faceCenter.position.ToString("F4") : "-") + " chinScale " + rig.chinScale + " k " + rig.transform.lossyScale.ToString("F3") + " light " + (rig.lightTransform != null ? rig.lightTransform.forward.ToString("F3") : "-"));
                var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/TestSet/Ch07.fbx"));
                Bounds head; int hv; FBX2NPRParseBake.HeadBounds(pbr, FBX2NPRRuntimeCheck.Crit("Ch07"), out head, out hv);
                L("Ch07 PBR head bounds " + head.center.ToString("F4") + " size " + head.size.ToString("F4"));
                UnityEngine.Object.DestroyImmediate(pbr); UnityEngine.Object.DestroyImmediate(toon);
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally { Flush("log_probe.txt"); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        }
    }
}
