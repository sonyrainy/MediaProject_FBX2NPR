// FBX2NPRFeatureSheet.cs — 이목구비 그리기(FeatureDrawCore) 수정의 비포/애프터용. Ch07 을 툰 모드로 다시 변환하고 같은 구도(얼굴·고개 돌림·전신)로 찍는다.
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRFeatureSheet.Run -OutFolder "NormalTestOut\runtime\features"  (환경변수 FBX2NPR_FEATURE_MODEL 로 모델 이름, 기본 Ch07; FBX2NPR_FEATURE_TAG 로 파일 접두어, 기본 after)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRFeatureSheet
    {
        static string OutDir { get { return Path.Combine(FBX2NPRRuntimeCheck.OutRoot, "features"); } }

        /// <summary>진단: 입꼬리의 어두운 점이 어느 텍셀에서 오는지. 얼굴 텍스처를 (A) '관측 안 됨' 라벨 텍셀만 자홍, (B) 입술 상자 밖 전부 자홍으로 바꿔 끼우고 얼굴을 찍는다.</summary>
        public static void CornerDebug()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                string model = "Ch07"; string texName = "Ch07_1001_Diffuse";
                var flat = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/FBX2NPRConverted/Ch07/Textures/" + texName + "_flat.png");
                var labPng = new Texture2D(2, 2); labPng.LoadImage(File.ReadAllBytes(Path.Combine(OutDir, texName + "_draw_label.png")));
                int W = flat.width, H = flat.height; var px = RegionFlatBake.Read(flat, W, H); var lab = labPng.GetPixels32();
                log.Add("텍스처 " + W + "x" + H + ", 라벨 PNG " + labPng.width + "x" + labPng.height);
                var variants = new[] { "unobserved", "outsideLips", "noShading", "lineOnly", "uvmap" };
                for (int v = 0; v < variants.Length; v++)
                {
                    var dbg = new Color32[W * H]; int n = 0;
                    for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x; dbg[i] = px[i];
                        bool mark = v == 0 ? (lab[i].r == 255) : v == 1 ? !(x >= 1400 && x <= 1560 && y >= 340 && y <= 440) : false;
                        if (v == 3) { int lum = (px[i].r * 54 + px[i].g * 183 + px[i].b * 19) >> 8; bool dark = lum < 110 && x >= 1400 && x <= 1560 && y >= 340 && y <= 440; dbg[i] = dark ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255); if (dark) n++; }   // 입술 상자 안의 어두운 텍셀(입선)만 검정, 나머지 흰색
                        if (v == 4) dbg[i] = new Color32((byte)(x * 255 / (W - 1)), (byte)(y * 255 / (H - 1)), 0, 255);   // 텍셀 좌표를 색으로 (R = x, G = y, 8텍셀 해상도)
                        if (mark) { dbg[i] = new Color32(255, 0, 255, 255); n++; }
                    }
                    var t = new Texture2D(W, H, TextureFormat.RGBA32, v != 4, false); t.SetPixels32(dbg); t.Apply(v != 4, false); t.hideFlags = HideFlags.HideAndDontSave;   // uvmap 은 밉맵 없이. sRGB 텍스처 + Unlit 이면 PNG 값 = 텍스처 값 (항등)
                    if (v == 4) t.filterMode = FilterMode.Point;
                    Material unlit = null;
                    if (v == 4) { var ush = Shader.Find("Universal Render Pipeline/Unlit"); if (ush != null) { unlit = new Material(ush); unlit.hideFlags = HideFlags.HideAndDontSave; unlit.SetTexture("_BaseMap", t); } }
                    FBX2NPRRuntimeCheck.NewStage(false);
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath("Ch07_Toon")));
                    var rig = go.GetComponentInChildren<ToonFaceRig>(true);
                    if (v >= 2 && rig != null) { rig.skinShadow = Color.white; rig.bodyShadowColor = Color.white; rig.noseShadow = false; rig.rim = 0f; rig.outline = false; }   // 명암·코 음영·외곽선 없이 텍스처만
                    if (feat != null && rig != null) { rig.ApplyOutline(); feat.settings.thickness = rig.outlineThickness * 2f; }
                    go.SetActive(false); go.SetActive(true); if (rig != null) rig.Apply();
                    var block = new MaterialPropertyBlock(); int swapped = 0;
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    {
                        var mats = r.sharedMaterials;
                        for (int k = 0; k < mats.Length; k++)
                        {
                            if (mats[k] == null) continue;
                            Texture cur = null; foreach (var pn in new[] { "_Albedo", "_BaseMap", "_MainTex" }) if (mats[k].HasProperty(pn)) { cur = mats[k].GetTexture(pn); if (cur != null) break; }
                            if (cur != flat) continue;
                            if (unlit != null) { mats[k] = unlit; r.sharedMaterials = mats; swapped++; continue; }   // 씬 인스턴스의 재질만 바꾼다 (에셋은 그대로)
                            r.GetPropertyBlock(block, k); block.SetTexture("_Albedo", t); block.SetTexture("_BaseMap", t); block.SetTexture("_MainTex", t); r.SetPropertyBlock(block, k); swapped++;
                        }
                    }
                    var notes = new List<string>(); var head = FBX2NPRRuntimeCheck.HeadOf(go, model, notes);
                    FBX2NPRCompare.SetSS(2); FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, "debug_" + variants[v] + "_face.png"), Vector3.forward, false, 1.18f); FBX2NPRCompare.SetSS(1);
                    log.Add(variants[v] + ": 자홍 텍셀 " + n + ", 바꿔 낀 재질 슬롯 " + swapped);
                    if (v == 4)
                    {
                        // 렌더에서 사용자가 칠한 영역(700² 얼굴 샷 기준 x 385~412, y 500~535)과 왼쪽 대칭 영역(x 288~315)의 텍셀 좌표를 읽는다
                        var shot = new Texture2D(2, 2); shot.LoadImage(File.ReadAllBytes(Path.Combine(OutDir, "debug_uvmap_face.png")));
                        foreach (var box in new[] { new[] { 385, 500, 412, 535, 0 }, new[] { 288, 500, 315, 535, 1 } })
                        {
                            var sb = new StringBuilder(box[4] == 0 ? "오른쪽(칠한 곳) " : "왼쪽(대칭) ");
                            for (int sy = box[1]; sy <= box[3]; sy += 7) for (int sx = box[0]; sx <= box[2]; sx += 9)
                            {
                                var c = shot.GetPixel(sx, shot.height - 1 - sy);   // PNG 는 위가 0 행, GetPixel 은 아래가 0
                                sb.Append(string.Format("({0},{1})->tx({2:F0},{3:F0}) ", sx, sy, c.r * (W - 1), c.g * (H - 1)));
                            }
                            log.Add(sb.ToString());
                        }
                        // 렌더→텍셀 대응 훑기: 입선 행(y 505)과 눈 행(y 350)을 가로로, 얼굴 가운데 열(x 350)을 세로로
                        foreach (var row in new[] { 505, 350 })
                        {
                            var sb = new StringBuilder("가로 y=" + row + ": ");
                            for (int sx = 270; sx <= 430; sx += 8) { var c = shot.GetPixel(sx, shot.height - 1 - row); sb.Append(string.Format("{0}->({1:F0},{2:F0}) ", sx, c.r * (W - 1), (H - 1) - c.g * (H - 1))); }
                            log.Add(sb.ToString());
                        }
                        {
                            var sb = new StringBuilder("세로 x=350: ");
                            for (int sy = 300; sy <= 580; sy += 10) { var c = shot.GetPixel(350, shot.height - 1 - sy); sb.Append(string.Format("{0}->({1:F0},{2:F0}) ", sy, c.r * (W - 1), (H - 1) - c.g * (H - 1))); }
                            log.Add(sb.ToString());
                        }
                        UnityEngine.Object.DestroyImmediate(shot);
                    }
                    UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(t); if (unlit != null) UnityEngine.Object.DestroyImmediate(unlit);
                }
                UnityEngine.Object.DestroyImmediate(labPng);
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.SetActive(featWas); }
                File.WriteAllText(Path.Combine(OutDir, "log_corner_debug.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FeatureSheet] " + l);
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            }
        }

        public static void Run()
        {
            string model = Environment.GetEnvironmentVariable("FBX2NPR_FEATURE_MODEL"); if (string.IsNullOrEmpty(model)) model = "Ch07";
            string tag = Environment.GetEnvironmentVariable("FBX2NPR_FEATURE_TAG"); if (string.IsNullOrEmpty(tag)) tag = "after";
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                string modelPath = null;
                foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), model, StringComparison.OrdinalIgnoreCase)) { modelPath = p; break; }
                }
                if (modelPath == null) { log.Add("모델 없음: " + model); return; }
                // 1) 툰 모드로 다시 변환 (그리기 입력도 이 폴더에 남긴다)
                FBX2NPRConverter.DebugDir = OutDir; FBX2NPRConverter.StyleOverride = 0;
                FBX2NPRConverter.Report rep;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { rep = FBX2NPRConverter.Convert(modelPath); } finally { FBX2NPRConverter.StyleOverride = -1; FBX2NPRConverter.DebugDir = null; }
                sw.Stop(); AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
                log.Add("==================== 툰 변환: " + model + " (" + (sw.ElapsedMilliseconds / 1000.0).ToString("F1") + "초)");
                foreach (var l in rep.lines) log.Add("  " + l);
                if (rep.prefabPath == null) { log.Add("프리팹 없음"); return; }
                // 2) 같은 구도로 찍기: 얼굴 700², 고개 35° 돌림, 전신 700x900 (FBX2NPRPainterlyCompare 와 같은 규칙)
                FBX2NPRRuntimeCheck.NewStage(false);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(rep.prefabPath));
                var rig = go.GetComponentInChildren<ToonFaceRig>(true);
                if (feat != null && rig != null) { rig.ApplyOutline(); feat.settings.thickness = rig.outlineThickness * 2f; }
                go.SetActive(false); go.SetActive(true); if (rig != null) rig.Apply();
                var notes = new List<string>(); var head = FBX2NPRRuntimeCheck.HeadOf(go, model, notes); var body = FBX2NPRRuntimeCheck.RendererBounds(go);
                var crit = FBX2NPRRuntimeCheck.Crit(model);
                FBX2NPRCompare.SetSS(2);
                FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, tag + "_face.png"), Vector3.forward, false, 1.18f);
                FBX2NPRCompare.Shoot(body, 700, 900, Path.Combine(OutDir, tag + "_body.png"), Vector3.forward, false, 1.12f);
                var hb = FBX2NPRConverter.FindHeadBone(go.transform, crit.headBoneContains);
                if (hb != null)
                {
                    var rot0 = hb.rotation; hb.rotation = Quaternion.AngleAxis(35f, Vector3.up) * hb.rotation; go.SetActive(false); go.SetActive(true); if (rig != null) rig.Apply();
                    FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, tag + "_turn.png"), Vector3.forward, false, 1.18f);
                    hb.rotation = rot0;
                }
                FBX2NPRCompare.SetSS(1);
                foreach (var n in notes) log.Add("  " + n);
                log.Add("찍음: " + tag + "_face/_turn/_body");
                UnityEngine.Object.DestroyImmediate(go);
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.SetActive(featWas); }
                File.WriteAllText(Path.Combine(OutDir, "log_features.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FeatureSheet] " + l);
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            }
        }
    }
}
