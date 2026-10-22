// FBX2NPRPainterlyCompare.cs — 회화풍 모드 비교 렌더. 4체를 회화풍으로 변환(StyleOverride = 1)하고
// 원본(Lit) | 툰 모드(있으면) | 회화풍(잉크 켬) | 회화풍(잉크 끔) 을 얼굴·고개 돌림·전신 구도로 찍는다. 결과 NormalTestOut/paint/.
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRPainterlyCompare.Run
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRPainterlyCompare
    {
        const float AmbientLinear = 0.30f;
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "paint"); } }

        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var log = new List<string>(); Directory.CreateDirectory(OutDir);
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            var temps = new List<UnityEngine.Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var only = Environment.GetEnvironmentVariable("FBX2NPR_ONLY");
                var models = new List<string>();
                foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (!p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                    string c = Path.GetFileNameWithoutExtension(p);
                    if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(';'), c) < 0) continue;
                    models.Add(p);
                }
                // 1) 회화풍 변환
                var reports = new List<FBX2NPRConverter.Report>();
                FBX2NPRConverter.StyleOverride = 1;
                try
                {
                    foreach (var m in models)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var r = FBX2NPRConverter.Convert(m); sw.Stop();
                        log.Add(""); log.Add("==================== 회화풍 변환: " + Path.GetFileNameWithoutExtension(m) + " (" + (sw.ElapsedMilliseconds / 1000.0).ToString("F1") + "초)");
                        foreach (var l in r.lines) log.Add("  " + l);
                        reports.Add(r);
                    }
                }
                finally { FBX2NPRConverter.StyleOverride = -1; }
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

                // 2) 비교 렌더
                foreach (var r in reports)
                {
                    if (r.prefabPath == null) continue;
                    string c = Path.GetFileNameWithoutExtension(r.model);
                    log.Add(""); log.Add("==================== 비교 렌더: " + c);
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(AmbientLinear, AmbientLinear, AmbientLinear, 1f).gamma; RenderSettings.reflectionIntensity = 0f;
                    var sunGo = new GameObject("Sun"); var sun = sunGo.AddComponent<Light>(); sun.type = LightType.Directional; sun.intensity = 1f; sun.color = Color.white; sun.shadows = LightShadows.None;
                    sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.LightDir(35f, 25f), Vector3.up); RenderSettings.sun = sun;
                    var imp = AssetImporter.GetAtPath(r.model);
                    var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();

                    var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(r.model));
                    foreach (var n in FBX2NPRCompare.FixOriginal(pbr, crit, r.model, temps)) log.Add("  " + n);
                    string toonPath = FBX2NPRConverter.OutRoot + "/" + FBX2NPRConverter.Safe(c) + "/" + FBX2NPRConverter.Safe(c) + "_Toon.prefab";
                    var toonPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(toonPath);
                    var toon = toonPrefab != null ? (GameObject)PrefabUtility.InstantiatePrefab(toonPrefab) : null;
                    var paint = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(r.prefabPath));
                    var trig = toon != null ? toon.GetComponentInChildren<ToonFaceRig>(true) : null;
                    var prig = paint.GetComponentInChildren<PainterlyRig>(true);
                    Bounds head; int hv; if (!FBX2NPRParseBake.HeadBounds(pbr, crit, out head, out hv)) head = r.head;
                    var body = new Bounds(); bool bInit = false;
                    foreach (var rr in pbr.GetComponentsInChildren<Renderer>()) { if (!bInit) { body = rr.bounds; bInit = true; } else body.Encapsulate(rr.bounds); }
                    var gos = new[] { pbr, toon, paint, paint }; var names = new[] { "orig", "toon", "paint_ink", "paint_noink" };
                    FBX2NPRCompare.SetSS(2);
                    for (int k = 0; k < gos.Length; k++)
                    {
                        var go = gos[k]; if (go == null) continue;
                        pbr.SetActive(go == pbr); if (toon != null) toon.SetActive(go == toon); paint.SetActive(go == paint);
                        if (feat != null)
                        {
                            if (k == 0) feat.SetActive(false);
                            else if (k == 1) { if (trig != null) { trig.ApplyOutline(); feat.settings.thickness = trig.outlineThickness * 2f; trig.Apply(); } feat.SetActive(true); }
                            else if (prig != null) { prig.inkLine = k == 2 ? crit.painterlyInk : 0f; prig.ApplyInk(); if (k == 2) feat.settings.thickness = prig.inkThickness * 2f; }
                        }
                        var hb = FBX2NPRConverter.FindHeadBone(go.transform, crit.headBoneContains);
                        go.SetActive(false); go.SetActive(true); if (go == toon && trig != null) trig.Apply();
                        FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + "_" + names[k] + "_face.png"), Vector3.forward, false, 1.18f);
                        FBX2NPRCompare.Shoot(body, 700, 900, Path.Combine(OutDir, c + "_" + names[k] + "_body.png"), Vector3.forward, false, 1.12f);
                        if (hb != null)
                        {
                            var rot0 = hb.rotation; hb.rotation = Quaternion.AngleAxis(35f, Vector3.up) * hb.rotation; go.SetActive(false); go.SetActive(true); if (go == toon && trig != null) trig.Apply();
                            FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + "_" + names[k] + "_turn.png"), Vector3.forward, false, 1.18f);
                            hb.rotation = rot0; go.SetActive(false); go.SetActive(true);
                        }
                        log.Add("  찍음: " + names[k]);
                    }
                    FBX2NPRCompare.SetSS(1);
                    UnityEngine.Object.DestroyImmediate(pbr); if (toon != null) UnityEngine.Object.DestroyImmediate(toon); UnityEngine.Object.DestroyImmediate(paint);
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                File.WriteAllText(Path.Combine(OutDir, "log_painterly.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[Painterly] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
    }
}
