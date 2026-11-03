// FBX2NPRShaderCompare.cs — 셰이더별 비교표. 어떤 셰이더를 써도 FBX2NPR 이 얼굴 음영 얼룩을 줄이면서 얼굴 안쪽 선을 지키는지 숫자로 잰다.
//   조건(행): 원본 노멀 / 노멀 정리만(Blender Normal Edit 방사형과 같은 처리, 선도 정리된 노멀에서 찾음) / FBX2NPR(정리된 노멀 + 원본 노멀 선 패스)
//   얼굴 얼룩 수: GeneralizationTest 의 클레이 렌더(텍스처 없이 흰색) 얼굴 안 음영 조각 수, 정면/측면. FBX2NPR 의 음영 노멀은 '노멀 정리만'과 같다.
//   얼굴 안쪽 선 보존율: 원본 노멀이 만드는 얼굴 안쪽 선 가운데 그 조건에서도 2픽셀 안에 선이 남은 비율, 정면/고개 35도 돌림.
//     노멀로 생기는 선만 잰다(깊이로 생기는 선은 어느 조건이든 같다). 얼굴 메시가 덮는 화면(눈·입 구멍은 채움)에서 바깥 윤곽 4픽셀은 뺀다
//     (배경과의 노멀 차는 늘 선이 된다). 기준 선은 ToonBand 에 원본 노멀을 넣어 찾은 선이다.
//   셰이더: ToonBand(변환 재질), Unity Toon Shader(Toon/Toon), lilToon(설치돼 있을 때만), URP Lit(선만: 단계 음영이 아니라 얼룩 수는 재지 않음).
//     UTS·lilToon 은 ToonBand 와 같은 단계 경계·그림자 색으로 맞춘다(GeneralizationTest.MakeUtsMat / MakeLilMat). 각 셰이더 본래의 꾸민 모습이 아니라 같은 조건의 대조다.
// 유니티짱은 손그림 얼굴이라 안쪽 선을 일부러 그리지 않는다(실루엣만) → 얼룩 수만 잰다.
// 프리팹·재질·메시 에셋은 바꾸지 않는다(사본만 쓰고 지운다). 외곽선 피처 값은 끝나면 되돌린다.
// 결과: NormalTestOut/shader_compare/ 의 blotch.csv, lines.csv, table.md 와 확인용 PNG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRShaderCompare
    {
        static readonly string[] LineChars = { "Ch07", "TheBoss", "Jones" };
        static readonly string[] BlotchChars = { "Ch07", "TheBoss", "Jones", "unitychan" };
        static readonly string[] Shaders = { "ToonBand", "UTS", "lilToon", "URP Lit" };
        const int Res = 700;
        const float TurnDeg = 35f;
        const byte LineOn = 77;          // 선만 보기 PNG 에서 선으로 볼 값 (sRGB 0.3)
        const int Tolerance = 2;         // 이 거리(픽셀) 안에 선이 있으면 남은 것으로 본다
        const int SilhouetteBand = 4;    // 얼굴 바깥 윤곽에서 이만큼은 재지 않는다

        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "shader_compare"); } }
        static readonly StringBuilder Log = new StringBuilder();
        static void L(string s) { Log.AppendLine(s); Debug.Log("[ShaderCompare] " + s); }

        [MenuItem("FBX2NPR/셰이더별 비교표 (얼룩 수 + 얼굴 안쪽 선 보존율)")]
        static void Menu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            RunBlotch(); RunLines(""); Debug.Log(WriteTable());
        }

        struct FeatState { public bool active, restore; public float darkness, thickness, depthScale, normalScale, threshold, debug; }
        static FeatState Keep(SobelOutlineFeature f)
        {
            var s = f.settings;
            return new FeatState { active = f.isActive, restore = s.restoreOriginalNormals, darkness = s.darkness, thickness = s.thickness, depthScale = s.depthScale, normalScale = s.normalScale, threshold = s.threshold, debug = s.debug };
        }
        static void Restore(SobelOutlineFeature f, FeatState k)
        {
            var s = f.settings;
            s.restoreOriginalNormals = k.restore; s.darkness = k.darkness; s.thickness = k.thickness; s.depthScale = k.depthScale;
            s.normalScale = k.normalScale; s.threshold = k.threshold; s.debug = k.debug;
            f.SetActive(k.active);
        }

        // ---------------------------------------------------------------- 얼굴 얼룩 수

        /// <summary>GeneralizationTest 를 UTS·lilToon 대조까지 켜고(변형 regress+uts+lil) 돌려 얼굴 안 음영 조각 수를 blotch.csv 로 남긴다.</summary>
        public static string RunBlotch()
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.isDirty) return "열린 씬에 저장하지 않은 변경이 있습니다: " + scene.path;
            string prevScene = scene.path;
            Directory.CreateDirectory(OutDir);
            var feat = FBX2NPRConverter.FindFeature();
            if (feat == null) return "외곽선 피처 없음";
            var keep = Keep(feat); bool asyncWas = ShaderUtil.allowAsyncCompilation;
            string variantWas = Environment.GetEnvironmentVariable("FBX2NPR_VARIANT"), onlyWas = Environment.GetEnvironmentVariable("FBX2NPR_ONLY");
            var sb = new StringBuilder("char,shader,cond,front,side\n");
            try
            {
                // 빈 씬에서 잰다 (열린 씬의 캐릭터·조명이 클레이 렌더에 섞이지 않게)
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Environment.SetEnvironmentVariable("FBX2NPR_VARIANT", "regress+uts+lil");
                Environment.SetEnvironmentVariable("FBX2NPR_ONLY", null);
                GeneralizationTest.Run();
                foreach (var c in BlotchChars)
                {
                    Dictionary<string, int[]> comps;
                    if (!GeneralizationTest.LastComps.TryGetValue(c, out comps)) { sb.AppendLine(c + ",-,-,-,-"); continue; }
                    BlotchRows(sb, c, "ToonBand", comps, "mix0.0", "mix1.0");
                    BlotchRows(sb, c, "UTS", comps, "uts_mix0.0", "uts_mix1.0");
                    BlotchRows(sb, c, "lilToon", comps, "lil_mix0.0", "lil_mix1.0");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("FBX2NPR_VARIANT", variantWas); Environment.SetEnvironmentVariable("FBX2NPR_ONLY", onlyWas);
                Restore(feat, keep); ShaderUtil.allowAsyncCompilation = asyncWas;
                if (!string.IsNullOrEmpty(prevScene)) EditorSceneManager.OpenScene(prevScene);
            }
            File.WriteAllText(Path.Combine(OutDir, "blotch.csv"), sb.ToString(), new UTF8Encoding(true));
            return sb.ToString();
        }

        static void BlotchRows(StringBuilder sb, string c, string sh, Dictionary<string, int[]> comps, string k0, string k1)
        {
            int[] a, b;
            if (!comps.TryGetValue(k0, out a) || !comps.TryGetValue(k1, out b)) return;
            // FBX2NPR 의 음영 노멀은 '노멀 정리만'과 같다 (달라지는 것은 선뿐)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},orig,{2},{3}\n{0},{1},clean,{4},{5}\n{0},{1},fbx2npr,{4},{5}\n", c, sh, a[0], a[1], b[0], b[1]);
        }

        // ---------------------------------------------------------------- 얼굴 안쪽 선 보존율

        class FaceSlot { public Renderer r; public Mesh clean, orig; public Material[] mats; }

        /// <summary>chars 가 비면 Ch07·TheBoss·Jones. 결과 lines.csv, log_lines.txt 와 조건별 PNG(_edges 선만, _final 최종, _check 보존 확인).</summary>
        public static string RunLines(string chars)
        {
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.isDirty) return "열린 씬에 저장하지 않은 변경이 있습니다: " + scene.path;
            string prevScene = scene.path;
            Directory.CreateDirectory(OutDir); Log.Clear();
            var feat = FBX2NPRConverter.FindFeature();
            if (feat == null) return "외곽선 피처 없음";
            var keep = Keep(feat); bool asyncWas = ShaderUtil.allowAsyncCompilation;
            var csv = new StringBuilder("char,view,shader,cond,refPx,keptPct,linePx,newPct\n");
            var temps = new List<UnityEngine.Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                FBX2NPRCompare.SetSS(1);
                foreach (var c in string.IsNullOrEmpty(chars) ? LineChars : chars.Split(';'))
                    LinesOne(c, feat, csv, temps);
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                Restore(feat, keep); ShaderUtil.allowAsyncCompilation = asyncWas;
                File.WriteAllText(Path.Combine(OutDir, "lines.csv"), csv.ToString(), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(OutDir, "log_lines.txt"), Log.ToString(), new UTF8Encoding(true));
                if (!string.IsNullOrEmpty(prevScene)) EditorSceneManager.OpenScene(prevScene);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            return csv.ToString();
        }

        static void LinesOne(string c, SobelOutlineFeature feat, StringBuilder csv, List<UnityEngine.Object> temps)
        {
            var toonP = AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath(c + "_Toon"));
            string mp = FBX2NPRHumanoidSetup.ModelPath(c);
            if (toonP == null || mp == null) { L(c + ": 툰 프리팹이나 원본 모델 없음"); return; }
            FBX2NPRRuntimeCheck.NewStage(false);
            var crit = FBX2NPRRuntimeCheck.Crit(c);
            // 구도는 FinalSheet 얼굴 컷과 같게 (원본 모델의 머리 경계)
            var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));
            Bounds head; int hv;
            if (!FBX2NPRParseBake.HeadBounds(pbr, crit, out head, out hv)) head = FBX2NPRRuntimeCheck.HeadOf(pbr, c, new List<string>());
            UnityEngine.Object.DestroyImmediate(pbr);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(toonP);
            // 스킨 메시는 프레임마다 한 번만 다시 계산된다. 이 도구는 한 프레임 안에서 메시를 바꾸고 고개를 돌려 가며 여러 장 찍으므로,
            // 찍을 때마다 다시 계산하게 한다 (켜지 않으면 첫 촬영 때 모습이 계속 쓰여 '원본 노멀'과 고개 돌림이 찍히지 않았다).
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            var rig = go.GetComponentInChildren<ToonFaceRig>(true);
            if (rig == null || rig.faceRenderers.Count == 0) { L(c + ": 얼굴 리그 없음"); UnityEngine.Object.DestroyImmediate(go); return; }
            float shippedFade = rig.noseLineFade;
            var slots = new List<FaceSlot>();
            foreach (var r in rig.faceRenderers)
            {
                var m = MeshOf(r); if (m == null) continue;
                var s = new FaceSlot { r = r, clean = m, mats = r.sharedMaterials, orig = OrigNormalMesh(m) };
                temps.Add(s.orig); slots.Add(s);
            }
            var conds = new List<string> { "orig", "clean", "fbx2npr" };
            if (shippedFade > 0f) conds.Add("fbx2npr_nofade");   // 코 둘레 선 지우기를 켠 캐릭터(Ch07)는 끈 경우도 잰다
            L(c + ": 얼굴 렌더러 " + slots.Count + "개, 코 둘레 선 지우기 " + shippedFade);

            var matCache = new Dictionary<string, Material>();
            var hb = FBX2NPRConverter.FindHeadBone(go.transform, crit.headBoneContains);
            Quaternion hbRot = hb != null ? hb.rotation : Quaternion.identity;
            foreach (var view in new[] { "front", "turn" })
            {
                if (hb != null) hb.rotation = view == "turn" ? Quaternion.AngleAxis(TurnDeg, Vector3.up) * hbRot : hbRot;
                string tag = c + "_" + view;
                SetCond(rig, slots, "clean", "ToonBand", shippedFade, matCache, temps);   // 얼굴 영역은 정리된 메시로 (정점이 같아 어느 쪽이든 같다)
                var region = FaceRegion(go, slots, head, Path.Combine(OutDir, tag + "_region.png"), feat, temps);
                int regionPx = 0; foreach (var b in region) if (b) regionPx++;
                L("  " + tag + ": 잴 영역 " + regionPx + " px");

                byte[] refG = null; bool[] refDil = null;
                foreach (var sh in Shaders)
                {
                    if (!SetCond(rig, slots, "orig", sh, shippedFade, matCache, temps)) { L("  " + sh + ": 셰이더 없음, 건너뜀"); continue; }
                    foreach (var cond in conds)
                    {
                        SetCond(rig, slots, cond, sh, shippedFade, matCache, temps);
                        string baseName = tag + "_" + sh.Replace(" ", "") + "_" + cond;
                        var g = Shoot(feat, rig, cond, head, baseName);
                        if (refG == null)
                        {
                            // 첫 촬영(ToonBand · 원본 노멀)이 기준 선이다
                            refG = g; refDil = Dilate(Threshold(g), Tolerance);
                        }
                        var cur = Threshold(g); var curDil = Dilate(cur, Tolerance);
                        int refPx = 0, kept = 0, linePx = 0, extra = 0;
                        var check = new Color32[g.Length];
                        for (int i = 0; i < g.Length; i++)
                        {
                            if (!region[i]) { check[i] = new Color32(0, 0, 0, 255); continue; }
                            bool rOn = refG[i] > LineOn;
                            if (cur[i]) linePx++;
                            if (rOn)
                            {
                                refPx++;
                                if (curDil[i]) { kept++; check[i] = new Color32(255, 255, 255, 255); }   // 남은 기준 선: 흰색
                                else check[i] = new Color32(255, 40, 40, 255);                          // 사라진 기준 선: 빨강
                            }
                            else if (cur[i] && !refDil[i]) { extra++; check[i] = new Color32(40, 220, 255, 255); }   // 기준에 없던 새 선: 하늘색
                            else check[i] = new Color32(45, 45, 50, 255);
                        }
                        SavePng(check, Path.Combine(OutDir, baseName + "_check.png"));
                        double keptPct = refPx > 0 ? 100.0 * kept / refPx : 0, newPct = refPx > 0 ? 100.0 * extra / refPx : 0;
                        csv.AppendFormat(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4},{5:F1},{6},{7:F1}\n", c, view, sh, cond, refPx, keptPct, linePx, newPct);
                        L(string.Format(CultureInfo.InvariantCulture, "    {0,-8} {1,-15} 기준 선 {2,6} px 중 남음 {3,5:F1}%  선 {4,6} px  새 선 {5,5:F1}%", sh, cond, refPx, keptPct, linePx, newPct));
                    }
                }
            }
            if (hb != null) hb.rotation = hbRot;
            UnityEngine.Object.DestroyImmediate(go);
        }

        static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer; if (smr != null) return smr.sharedMesh;
            var mf = r != null ? r.GetComponent<MeshFilter>() : null; return mf != null ? mf.sharedMesh : null;
        }
        static void SetMesh(Renderer r, Mesh m)
        {
            var smr = r as SkinnedMeshRenderer; if (smr != null) { smr.sharedMesh = m; return; }
            var mf = r.GetComponent<MeshFilter>(); if (mf != null) mf.sharedMesh = m;
        }

        // 원본 노멀 메시: 변환 메시의 노멀 자리에 UV3(물체 공간 원본 노멀)을 넣은 사본. 정점·스킨이 같아 깊이도 같다.
        static Mesh OrigNormalMesh(Mesh clean)
        {
            var m = UnityEngine.Object.Instantiate(clean); m.name = clean.name + "_origNormals";
            var uv3 = new List<Vector3>(); m.GetUVs(3, uv3);
            var n = m.normals;
            if (uv3.Count == n.Length)
            {
                for (int i = 0; i < n.Length; i++) if (uv3[i].sqrMagnitude > 1e-8f) n[i] = uv3[i].normalized;
                m.normals = n;
            }
            return m;
        }

        /// <summary>
        /// 조건에 맞게 얼굴 렌더러의 메시·재질과 리그 값을 바꾼다. 셰이더가 없으면 false.
        ///   orig    : 원본 노멀 메시. 선은 셰이더가 노멀 버퍼에 쓴 노멀(=원본)에서 찾는다.
        ///   clean   : 정리된 노멀 메시. 선도 노멀 버퍼(=정리된 노멀)에서 찾는다 — DCC 에서 노멀만 정리해 온 것과 같다.
        ///   fbx2npr : 정리된 노멀 메시 + 외곽선 피처의 원본 노멀 선 패스 (리그의 코 둘레 선 지우기 값 그대로)
        /// ToonBand 는 노멀 버퍼에도 원본 노멀을 쓰는 장치(_UseOrigNormal)가 있어 orig·clean 에서는 끈다.
        /// </summary>
        static bool SetCond(ToonFaceRig rig, List<FaceSlot> slots, string cond, string sh, float shippedFade, Dictionary<string, Material> cache, List<UnityEngine.Object> temps)
        {
            bool line = cond.StartsWith("fbx2npr");
            foreach (var s in slots)
            {
                var mats = new Material[s.mats.Length];
                for (int i = 0; i < mats.Length; i++)
                {
                    mats[i] = FaceMat(sh, s.mats[i], !line, cache, temps);
                    if (mats[i] == null) return false;
                }
                SetMesh(s.r, cond == "orig" ? s.orig : s.clean);
                s.r.sharedMaterials = mats;
            }
            rig.noseLineFade = cond == "fbx2npr" ? shippedFade : 0f;
            rig.ApplyOutline(); rig.Apply();
            if (sh != "ToonBand")
            {
                // 리그가 렌더러에 넣는 ToonBand 값 중 UTS 와 이름이 같은 _RimLight 는 0 으로 (선과는 무관, 최종 화면 색만)
                var mpb = new MaterialPropertyBlock();
                foreach (var s in slots)
                    for (int i = 0; i < Mathf.Max(1, s.mats.Length); i++)
                    {
                        if (s.mats.Length > 1) s.r.GetPropertyBlock(mpb, i); else s.r.GetPropertyBlock(mpb);
                        mpb.SetFloat("_RimLight", 0f);
                        if (s.mats.Length > 1) s.r.SetPropertyBlock(mpb, i); else s.r.SetPropertyBlock(mpb);
                    }
            }
            return true;
        }

        static Material FaceMat(string sh, Material toon, bool lineFromBuffer, Dictionary<string, Material> cache, List<UnityEngine.Object> temps)
        {
            if (toon == null) return null;
            if (sh == "ToonBand" && !lineFromBuffer) return toon;   // 변환 재질 그대로
            string key = sh + "|" + toon.GetInstanceID() + "|" + lineFromBuffer;
            Material m;
            if (cache.TryGetValue(key, out m)) return m;
            var tex = toon.HasProperty("_Albedo") ? toon.GetTexture("_Albedo") : null;
            switch (sh)
            {
                case "ToonBand": m = new Material(toon); m.SetFloat("_UseOrigNormal", 0f); break;
                case "UTS": m = GeneralizationTest.MakeUtsMat(tex, false, false); break;
                case "URP Lit": { var s = Shader.Find("Universal Render Pipeline/Lit"); if (s != null) { m = new Material(s); m.SetTexture("_BaseMap", tex); } break; }
                case "lilToon": m = GeneralizationTest.MakeLilMat(tex, false, false); break;
            }
            if (m != null) temps.Add(m);
            cache[key] = m;
            return m;
        }

        /// <summary>선만 보기(노멀 엣지만)와 최종 화면을 찍는다. 반환은 선만 보기의 회색값.</summary>
        static byte[] Shoot(SobelOutlineFeature feat, ToonFaceRig rig, string cond, Bounds head, string baseName)
        {
            var st = feat.settings;
            st.restoreOriginalNormals = cond.StartsWith("fbx2npr");
            string edges = Path.Combine(OutDir, baseName + "_edges.png");
            st.debug = 1f; st.depthScale = 0f;
            FBX2NPRCompare.Shoot(head, Res, Res, edges, Vector3.forward, false, 1.18f);
            st.debug = 0f; st.depthScale = rig.outlineDepthScale;
            FBX2NPRCompare.Shoot(head, Res, Res, Path.Combine(OutDir, baseName + "_final.png"), Vector3.forward, false, 1.18f);
            return Gray(edges);
        }

        /// <summary>얼굴 렌더러는 흰색, 나머지는 검은색으로 찍어 잴 영역을 정한다: 얼굴 안의 구멍(눈·입)은 채우고 바깥 윤곽에서 몇 픽셀은 뺀다.</summary>
        static bool[] FaceRegion(GameObject go, List<FaceSlot> slots, Bounds head, string file, SobelOutlineFeature feat, List<UnityEngine.Object> temps)
        {
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            var white = new Material(unlit); white.SetColor("_BaseColor", Color.white); temps.Add(white);
            var black = new Material(unlit); black.SetColor("_BaseColor", Color.black); temps.Add(black);
            var saved = new Dictionary<Renderer, Material[]>();
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                saved[r] = r.sharedMaterials;
                bool face = slots.Exists(s => s.r == r);
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = face ? white : black;
                r.sharedMaterials = mats;
            }
            bool act = feat.isActive; feat.SetActive(false);
            FBX2NPRCompare.Shoot(head, Res, Res, file, Vector3.forward, false, 1.18f);
            feat.SetActive(act);
            foreach (var kv in saved) kv.Key.sharedMaterials = kv.Value;
            var g = Gray(file);
            var m = new bool[g.Length];
            for (int i = 0; i < g.Length; i++) m[i] = g[i] > 128;
            FillHoles(m);
            return Erode(m, SilhouetteBand);
        }

        // ---------------------------------------------------------------- 화소 처리

        static byte[] Gray(string file)
        {
            var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(file));
            var px = t.GetPixels32(); var g = new byte[px.Length];
            for (int i = 0; i < px.Length; i++) g[i] = px[i].r;
            UnityEngine.Object.DestroyImmediate(t);
            return g;
        }
        static bool[] Threshold(byte[] g) { var m = new bool[g.Length]; for (int i = 0; i < g.Length; i++) m[i] = g[i] > LineOn; return m; }

        static void SavePng(Color32[] px, string file)
        {
            var t = new Texture2D(Res, Res, TextureFormat.RGBA32, false); t.SetPixels32(px); t.Apply();
            File.WriteAllBytes(file, t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
        }

        // 바깥(화면 가장자리)과 이어지지 않은 빈 곳을 채운다
        static void FillHoles(bool[] m)
        {
            var outside = new bool[m.Length]; var stack = new Stack<int>();
            for (int x = 0; x < Res; x++) { Seed(m, outside, stack, x, 0); Seed(m, outside, stack, x, Res - 1); }
            for (int y = 0; y < Res; y++) { Seed(m, outside, stack, 0, y); Seed(m, outside, stack, Res - 1, y); }
            while (stack.Count > 0)
            {
                int p = stack.Pop(), px = p % Res, py = p / Res;
                if (px > 0) Seed(m, outside, stack, px - 1, py);
                if (px < Res - 1) Seed(m, outside, stack, px + 1, py);
                if (py > 0) Seed(m, outside, stack, px, py - 1);
                if (py < Res - 1) Seed(m, outside, stack, px, py + 1);
            }
            for (int i = 0; i < m.Length; i++) if (!m[i] && !outside[i]) m[i] = true;
        }
        static void Seed(bool[] m, bool[] outside, Stack<int> stack, int x, int y)
        {
            int i = y * Res + x; if (m[i] || outside[i]) return;
            outside[i] = true; stack.Push(i);
        }

        // 정사각형 창의 최솟값(침식)·최댓값(팽창). 가로 한 번, 세로 한 번. 화면 밖은 false.
        static bool[] Erode(bool[] m, int r) { return Window(m, r, true); }
        static bool[] Dilate(bool[] m, int r) { return Window(m, r, false); }
        static bool[] Window(bool[] m, int r, bool all)
        {
            var tmp = new bool[m.Length]; var o = new bool[m.Length];
            for (int y = 0; y < Res; y++) for (int x = 0; x < Res; x++) tmp[y * Res + x] = Span(m, x, y, r, 1, 0, all);
            for (int y = 0; y < Res; y++) for (int x = 0; x < Res; x++) o[y * Res + x] = Span(tmp, x, y, r, 0, 1, all);
            return o;
        }
        static bool Span(bool[] m, int x, int y, int r, int dx, int dy, bool all)
        {
            for (int k = -r; k <= r; k++)
            {
                int xx = x + k * dx, yy = y + k * dy;
                bool v = xx >= 0 && yy >= 0 && xx < Res && yy < Res && m[yy * Res + xx];
                if (all && !v) return false;
                if (!all && v) return true;
            }
            return all;
        }

        // ---------------------------------------------------------------- 표

        static readonly Dictionary<string, string> CondName = new Dictionary<string, string>
        {
            { "orig", "원본 노멀" }, { "clean", "노멀 정리만" }, { "fbx2npr", "FBX2NPR" }, { "fbx2npr_nofade", "FBX2NPR (코 둘레 선 지우기 끔)" },
        };

        /// <summary>blotch.csv 와 lines.csv 를 읽어 셰이더별 표(table.md)를 쓴다.</summary>
        public static string WriteTable()
        {
            var blotch = ReadCsv(Path.Combine(OutDir, "blotch.csv"));
            var lines = ReadCsv(Path.Combine(OutDir, "lines.csv"));
            var sb = new StringBuilder();
            sb.AppendLine("# 셰이더별 비교표");
            sb.AppendLine();
            sb.AppendLine("- 얼굴 얼룩 수: 텍스처 없이 흰색으로 찍은 얼굴 안의 음영 조각 수 (정면 / 측면). 적을수록 깨끗하다. FBX2NPR 의 음영 노멀은 '노멀 정리만'과 같다.");
            sb.AppendLine("- 얼굴 안쪽 선 보존율: 원본 노멀로 생기는 얼굴 안쪽 선(눈·코·입 등, 노멀로 생기는 선만) 가운데 2픽셀 안에 선이 남은 비율 (정면 / 고개 35도 돌림). 기준은 ToonBand · 원본 노멀.");
            sb.AppendLine("- UTS·lilToon 은 ToonBand 와 같은 단계 경계·그림자 색으로 맞춘 대조다 (각 셰이더 본래의 꾸민 모습이 아니다). URP Lit 은 단계 음영이 아니라 얼룩 수를 재지 않는다.");
            sb.AppendLine("- 유니티짱의 UTS 얼룩 수는 이 설정에서 UTS 가 눈동자 카드의 알파 잘라내기를 하지 않아 눈 텍스처가 얼굴 영역에 섞여 다른 셰이더보다 크다 (얼굴 노멀과 무관).");
            sb.AppendLine();
            foreach (var sh in Shaders)
            {
                bool any = false;
                foreach (var r in lines) if (r[2] == sh) any = true;
                foreach (var r in blotch) if (r[1] == sh) any = true;
                if (!any) continue;
                sb.AppendLine("## " + sh);
                sb.AppendLine();
                sb.AppendLine("| 캐릭터 | 조건 | 얼굴 얼룩 수 | 얼굴 안쪽 선 보존율 |");
                sb.AppendLine("|---|---|---|---|");
                foreach (var c in BlotchChars)
                    foreach (var cond in CondName.Keys)
                    {
                        string b = "-", l = "-";
                        foreach (var r in blotch) if (r[0] == c && r[1] == sh && r[2] == cond) b = r[3] + " / " + r[4];
                        string lf = null, lt = null;
                        foreach (var r in lines) if (r[0] == c && r[2] == sh && r[3] == cond) { if (r[1] == "front") lf = r[5]; else lt = r[5]; }
                        if (lf != null || lt != null) l = (lf ?? "-") + "% / " + (lt ?? "-") + "%";
                        else if (c == "unitychan" && b != "-") l = "해당 없음 (손그림 얼굴)";
                        if (b == "-" && l == "-") continue;
                        sb.AppendLine("| " + c + " | " + CondName[cond] + " | " + b + " | " + l + " |");
                    }
                sb.AppendLine();
            }
            File.WriteAllText(Path.Combine(OutDir, "table.md"), sb.ToString(), new UTF8Encoding(true));
            return sb.ToString();
        }

        static List<string[]> ReadCsv(string file)
        {
            var rows = new List<string[]>();
            if (!File.Exists(file)) return rows;
            var all = File.ReadAllLines(file);
            for (int i = 1; i < all.Length; i++) if (all[i].Trim().Length > 0) rows.Add(all[i].Split(','));
            return rows;
        }
    }
}
