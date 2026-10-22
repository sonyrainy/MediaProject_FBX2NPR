// FBX2NPRFinalSheet.cs — 최종 상태 확인용. (1) 지정한 모델만 툰/회화풍으로 다시 변환하고 (2) 4체를 원본 | 툰 | 회화풍으로 같은 구도에서 찍는다.
// 구도·조명은 FBX2NPRPainterlyCompare 와 같다 (NormalTestOut/paint 의 예전 렌더와 차이를 잴 수 있게). 결과 NormalTestOut/final/.
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRFinalSheet.Run -OutFolder "NormalTestOut\final"
//   환경변수 FBX2NPR_FINAL_TOON (기본 "TheBoss;Jones;unitychan"), FBX2NPR_FINAL_PAINT (기본 "Ch07;TheBoss;Jones;unitychan") — 비우려면 "-"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRFinalSheet
    {
        static readonly string[] All = { "Ch07", "TheBoss", "Jones", "unitychan" };
        // 환경변수 FBX2NPR_FINAL_OUT (NormalTestOut 아래 폴더, 기본 "final"), FBX2NPR_FINAL_CHARS (찍을 캐릭터, 기본 전부), FBX2NPR_FINAL_NOSE (툰 리그 코 음영 세기 덮어쓰기, 비우면 그대로 — 프리팹은 저장하지 않는다)
        static string OutDir { get { string o = Environment.GetEnvironmentVariable("FBX2NPR_FINAL_OUT"); return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", string.IsNullOrEmpty(o) ? "final" : o); } }

        static string[] List(string env, string def)
        {
            string v = Environment.GetEnvironmentVariable(env); if (string.IsNullOrEmpty(v)) v = def; if (v == "-") return new string[0];
            return v.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }
        static string FindModel(string name)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public static void Run()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            var temps = new List<UnityEngine.Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                // 1) 다시 변환
                foreach (var pass in new[] { new { style = 0, list = List("FBX2NPR_FINAL_TOON", "TheBoss;Jones;unitychan"), name = "툰" }, new { style = 1, list = List("FBX2NPR_FINAL_PAINT", "Ch07;TheBoss;Jones;unitychan"), name = "회화풍" } })
                {
                    foreach (var m in pass.list)
                    {
                        string mp = FindModel(m); if (mp == null) { log.Add("모델 없음: " + m); continue; }
                        var sw = System.Diagnostics.Stopwatch.StartNew(); FBX2NPRConverter.Report rep;
                        FBX2NPRConverter.StyleOverride = pass.style;
                        try { rep = FBX2NPRConverter.Convert(mp); } finally { FBX2NPRConverter.StyleOverride = -1; }
                        sw.Stop();
                        log.Add(""); log.Add("==================== " + pass.name + " 변환: " + m + " (" + (sw.ElapsedMilliseconds / 1000.0).ToString("F1") + "초)");
                        foreach (var l in rep.lines) log.Add("  " + l);
                    }
                }
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
                // 2) 찍기
                string only = Environment.GetEnvironmentVariable("FBX2NPR_FINAL_CHARS");
                foreach (var c in All) { if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(';'), c) < 0) continue; Shoot(c, feat, log, temps); }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                File.WriteAllText(Path.Combine(OutDir, "log_final.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FinalSheet] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void Shoot(string c, SobelOutlineFeature feat, List<string> log, List<UnityEngine.Object> temps)
        {
            string mp = FindModel(c); if (mp == null) return;
            FBX2NPRRuntimeCheck.NewStage(false);
            var crit = FBX2NPRRuntimeCheck.Crit(c);
            var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));
            foreach (var n in FBX2NPRCompare.FixOriginal(pbr, crit, mp, temps)) log.Add("  [" + c + "] " + n);
            var toonP = AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath(c + "_Toon"));
            var paintP = AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath(c + "_Painterly"));
            var toon = toonP != null ? (GameObject)PrefabUtility.InstantiatePrefab(toonP) : null;
            var paint = paintP != null ? (GameObject)PrefabUtility.InstantiatePrefab(paintP) : null;
            var trig = toon != null ? toon.GetComponentInChildren<ToonFaceRig>(true) : null;
            var prig = paint != null ? paint.GetComponentInChildren<PainterlyRig>(true) : null;
            Bounds head; int hv; if (!FBX2NPRParseBake.HeadBounds(pbr, crit, out head, out hv)) head = FBX2NPRRuntimeCheck.HeadOf(pbr, c, new List<string>());
            var body = FBX2NPRRuntimeCheck.RendererBounds(pbr);
            var gos = new[] { pbr, toon, paint }; var names = new[] { "orig", "toon", "paint" };
            for (int k = 0; k < gos.Length; k++)
            {
                var go = gos[k]; if (go == null) { log.Add("  [" + c + "] 프리팹 없음: " + names[k]); continue; }
                foreach (var g in gos) if (g != null) g.SetActive(g == go);
                float baseThick = 1f;
                if (feat != null)
                {
                    if (k == 0) feat.SetActive(false);
                    else if (k == 1 && trig != null)
                    {
                        string nsEnv = Environment.GetEnvironmentVariable("FBX2NPR_FINAL_NORMALSCALE"); if (!string.IsNullOrEmpty(nsEnv)) trig.outlineNormalScale = float.Parse(nsEnv, System.Globalization.CultureInfo.InvariantCulture);   // 진단용 덮어쓰기(프리팹 저장 안 함)
                        string thEnv = Environment.GetEnvironmentVariable("FBX2NPR_FINAL_THRESHOLD"); if (!string.IsNullOrEmpty(thEnv)) trig.outlineThreshold = float.Parse(thEnv, System.Globalization.CultureInfo.InvariantCulture);
                        if (Environment.GetEnvironmentVariable("FBX2NPR_FINAL_NOOUTLINE") == "1") trig.outline = false;
                        trig.ApplyOutline(); baseThick = trig.outlineThickness;
                    }
                    else if (k == 2 && prig != null) { prig.ApplyInk(); baseThick = prig.inkThickness; }
                }
                if (go == toon && Environment.GetEnvironmentVariable("FBX2NPR_FINAL_DUMP") == "1")
                    foreach (var r in toon.GetComponentsInChildren<Renderer>(true))
                    {
                        var sb = new StringBuilder("  [dump] " + r.name + " active=" + r.gameObject.activeInHierarchy + " enabled=" + r.enabled + " :");
                        foreach (var m in r.sharedMaterials) { if (m == null) { sb.Append(" <null>"); continue; } var tx = m.HasProperty("_Albedo") ? m.GetTexture("_Albedo") : null; sb.Append(" " + m.name + "(" + m.shader.name + ", tex=" + (tx != null ? tx.name : "-") + ")"); }
                        log.Add(sb.ToString());
                    }
                string noseEnv = Environment.GetEnvironmentVariable("FBX2NPR_FINAL_NOSE");
                if (go == toon && trig != null && !string.IsNullOrEmpty(noseEnv)) trig.noseStrength = float.Parse(noseEnv, System.Globalization.CultureInfo.InvariantCulture);
                go.SetActive(false); go.SetActive(true); if (go == toon && trig != null) trig.Apply();
                // 가까이 (SS2, 선 두께 ×2 — 줄이면 원래 두께)
                if (feat != null && k > 0) feat.settings.thickness = baseThick * 2f;
                FBX2NPRCompare.SetSS(2);
                FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + "_" + names[k] + "_face.png"), Vector3.forward, false, 1.18f);
                FBX2NPRCompare.Shoot(body, 700, 900, Path.Combine(OutDir, c + "_" + names[k] + "_body.png"), Vector3.forward, false, 1.12f);
                // 게임 거리 (SS1, 원래 선 두께, 키 약 190px) — 런타임 검증 씬의 넓은 카메라와 비슷한 크기
                if (feat != null && k > 0) feat.settings.thickness = baseThick;
                FBX2NPRCompare.SetSS(1);
                FBX2NPRCompare.Shoot(body, 160, 205, Path.Combine(OutDir, c + "_" + names[k] + "_far.png"), Vector3.forward, false, 1.12f);
                log.Add("  [" + c + "] 찍음: " + names[k]);
            }
            foreach (var g in gos) if (g != null) UnityEngine.Object.DestroyImmediate(g);
        }
    }
}
