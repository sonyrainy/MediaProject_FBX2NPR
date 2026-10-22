// FBX2NPRInspectorShot.cs — (1) Run: GUI 에디터(-batchmode 없이 -executeMethod)에서 검증 씬을 열고 리그를 선택한 뒤 인스펙터 창을 화면에서 읽어 PNG 로 저장, 에디터 종료.
//     GUI 에디터는 executeMethod 직후 "Define symbols changed" 로 도메인을 다시 올려 update 등록이 사라진다(2차 시도에서 실측) → 진행 상태를 SessionState 에 두고 [InitializeOnLoad] 로 다시 등록한다.
// (2) SliderSheet: 배치에서 인스펙터 슬라이더 값을 바꿔 가며 얼굴을 찍어 '값을 바꾸면 바로 반영'을 그림으로 남긴다.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    [InitializeOnLoad]
    public static class FBX2NPRInspectorShot
    {
        const string KeyPending = "FBX2NPRShot.pending", KeyPhase = "FBX2NPRShot.phase", KeyT0 = "FBX2NPRShot.t0";
        static string OutDir { get { return Path.Combine(FBX2NPRRuntimeCheck.OutRoot, "inspector"); } }
        static string LogPath { get { return Path.Combine(OutDir, "log_inspector.txt"); } }
        static int phase; static double t0;

        static FBX2NPRInspectorShot()
        {
            if (!SessionState.GetBool(KeyPending, false)) return;
            phase = SessionState.GetInt(KeyPhase, 0); t0 = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            L("도메인 리로드 뒤 다시 등록, phase " + phase);
        }

        static void L(string s)
        {
            Debug.Log("[InspectorShot] " + s);
            try { Directory.CreateDirectory(OutDir); File.AppendAllText(LogPath, s + "\n", new UTF8Encoding(true)); } catch { }
        }
        static void SetPhase(int p) { phase = p; t0 = EditorApplication.timeSinceStartup; SessionState.SetInt(KeyPhase, p); }

        public static void Run()
        {
            Directory.CreateDirectory(OutDir); if (File.Exists(LogPath)) File.Delete(LogPath);
            L("시작: 배치 " + Application.isBatchMode + ", pixelsPerPoint " + EditorGUIUtility.pixelsPerPoint + ", 화면 " + Screen.currentResolution.width + "x" + Screen.currentResolution.height);
            SessionState.SetBool(KeyPending, true); SetPhase(0);
            EditorApplication.update += Tick;
        }

        static GameObject RigOf(string rootName, bool toon)
        {
            var go = GameObject.Find(rootName); if (go == null) return null;
            if (toon) { var r = go.GetComponentInChildren<ToonFaceRig>(true); return r != null ? r.gameObject : null; }
            var p = go.GetComponentInChildren<PainterlyRig>(true); return p != null ? p.gameObject : null;
        }

        static EditorWindow FindInspector()
        {
            var type = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
            EditorWindow best = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(type)) { var w = o as EditorWindow; if (w == null) continue; if (best == null || !w.docked) best = w; }
            return best;
        }

        static void Tick()
        {
            double dt = EditorApplication.timeSinceStartup - t0;
            try
            {
                if (phase == 0 && dt > 4.0)
                {
                    if (!File.Exists(FBX2NPRRuntimeCheck.ScenePath)) FBX2NPRRuntimeCheck.BuildScene();
                    EditorSceneManager.OpenScene(FBX2NPRRuntimeCheck.ScenePath, OpenSceneMode.Single);
                    var toonRig = RigOf("Ch07_Toon", true); var paintRig = RigOf("Ch07_Painterly", false);
                    L("씬 열림. 툰 리그 " + (toonRig != null ? toonRig.name : "없음") + ", 회화풍 리그 " + (paintRig != null ? paintRig.name : "없음"));
                    var type = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
                    var insp = (EditorWindow)ScriptableObject.CreateInstance(type);
                    insp.ShowUtility(); insp.position = new Rect(40, 40, 560, 1060); insp.Focus();
                    Selection.activeGameObject = toonRig; insp.Repaint(); InternalEditorUtility.RepaintAllViews();
                    foreach (var f in Directory.GetFiles(OutDir, "*_*.txt")) if (Path.GetFileName(f).StartsWith("ready_") || Path.GetFileName(f).StartsWith("done_")) File.Delete(f);
                    SetPhase(1);
                }
                else if (phase == 1 && dt > 3.0)
                {
                    // 화면 픽셀을 읽으면(ReadScreenPixel) 그 순간 앞에 뜬 다른 창이 찍힌다(3차 시도에서 실측). 창 내용은 바깥 스크립트(capwin.ps1)가 PrintWindow 로 받아 가므로
                    // 여기서는 '준비됨' 표식 파일을 쓰고 '끝' 표식을 기다린다. 선택을 바꾼 뒤 다시 그리기는 다음 틱에 된다.
                    L("툰 리그 선택됨, 선택 " + (Selection.activeGameObject != null ? Selection.activeGameObject.name : "없음"));
                    File.WriteAllText(Path.Combine(OutDir, "ready_toon.txt"), "ok"); SetPhase(2);
                }
                else if (phase == 2 && (File.Exists(Path.Combine(OutDir, "done_toon.txt")) || dt > 90.0))
                {
                    L(dt > 90.0 ? "툰 캡처 표식 대기 시간 초과" : "툰 캡처 끝");
                    // 포커스 없는 플로팅 창은 선택이 바뀌어도 다시 그려지지 않는다(4차 시도에서 실측) → 창을 닫고 새로 만든다 (새 창은 만들 때 현재 선택으로 그려진다)
                    var old = FindInspector(); if (old != null && !old.docked) old.Close();
                    Selection.activeGameObject = RigOf("Ch07_Painterly", false);
                    var type = typeof(Editor).Assembly.GetType("UnityEditor.InspectorWindow");
                    var insp2 = (EditorWindow)ScriptableObject.CreateInstance(type);
                    insp2.ShowUtility(); insp2.position = new Rect(40, 40, 560, 1060); insp2.Focus(); insp2.Repaint(); InternalEditorUtility.RepaintAllViews();
                    SetPhase(4);
                }
                else if (phase == 4 && dt > 3.0)
                {
                    L("회화풍 리그 선택됨, 선택 " + (Selection.activeGameObject != null ? Selection.activeGameObject.name : "없음"));
                    File.WriteAllText(Path.Combine(OutDir, "ready_paint.txt"), "ok"); SetPhase(5);
                }
                else if (phase == 5 && (File.Exists(Path.Combine(OutDir, "done_paint.txt")) || dt > 90.0))
                {
                    L(dt > 90.0 ? "회화풍 캡처 표식 대기 시간 초과" : "회화풍 캡처 끝");
                    SetPhase(3);
                }
                else if (phase == 3 && dt > 1.0)
                {
                    EditorApplication.update -= Tick; SessionState.EraseBool(KeyPending);
                    L("끝"); EditorApplication.Exit(0);
                }
            }
            catch (Exception ex)
            {
                L("예외: " + ex);
                EditorApplication.update -= Tick; SessionState.EraseBool(KeyPending);
                EditorApplication.Exit(1);
            }
        }

        static void Capture(EditorWindow insp, string name)
        {
            var r = insp.position; float pp = EditorGUIUtility.pixelsPerPoint;
            L(name + ": 창 " + r + ", pixelsPerPoint " + pp + ", 선택 " + (Selection.activeGameObject != null ? Selection.activeGameObject.name : "없음"));
            Save(name + "_pt.png", InternalEditorUtility.ReadScreenPixel(new Vector2(r.x, r.y), (int)r.width, (int)r.height), (int)r.width, (int)r.height);
            if (pp > 1.01f) Save(name + "_px.png", InternalEditorUtility.ReadScreenPixel(new Vector2(r.x * pp, r.y * pp), (int)(r.width * pp), (int)(r.height * pp)), (int)(r.width * pp), (int)(r.height * pp));
        }
        static void Save(string file, Color[] px, int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGB24, false); t.SetPixels(px); t.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, file), t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
            L("저장 " + file + " " + w + "x" + h);
        }

        /// <summary>배치: 슬라이더 값을 바꿔 가며 Ch07 툰·회화풍 얼굴을 찍는다 → NormalTestOut/runtime/inspector/slider_*.png</summary>
        public static void SliderSheet()
        {
            string outDir = OutDir; Directory.CreateDirectory(outDir); var log = new List<string>();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                log.Add(ShotToon(outDir, "slider_toon_default", r => { }));
                log.Add(ShotToon(outDir, "slider_toon_linePos+0.35", r => r.linePosition = 0.35f));
                log.Add(ShotToon(outDir, "slider_toon_chinTilt0.2", r => r.chinTilt = 0.2f));
                log.Add(ShotToon(outDir, "slider_toon_rim1.0", r => r.rim = 1f));
                log.Add(ShotToon(outDir, "slider_toon_outlineOff", r => r.outline = false));
                log.Add(ShotPaint(outDir, "slider_paint_default", r => { }));
                log.Add(ShotPaint(outDir, "slider_paint_eyeInk0", r => r.eyeInk = 0f));
                log.Add(ShotPaint(outDir, "slider_paint_eyeInk0.5", r => r.eyeInk = 0.5f));
                log.Add(ShotPaint(outDir, "slider_paint_inkLine0", r => r.inkLine = 0f));
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                File.WriteAllText(Path.Combine(outDir, "log_slider.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[InspectorShot] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static string ShotToon(string outDir, string name, Action<ToonFaceRig> set)
        {
            FBX2NPRRuntimeCheck.NewStage(false);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath("Ch07_Toon")));
            var rig = go.GetComponentInChildren<ToonFaceRig>(true); set(rig);
            var feat = FBX2NPRConverter.FindFeature();
            rig.ApplyOutline(); if (feat != null && rig.outline) feat.settings.thickness = rig.outlineThickness * 2f;
            go.SetActive(false); go.SetActive(true); rig.Apply();
            var notes = new List<string>(); var head = FBX2NPRRuntimeCheck.HeadOf(go, "Ch07", notes);
            FBX2NPRCompare.SetSS(2); FBX2NPRCompare.Shoot(head, 600, 600, Path.Combine(outDir, name + ".png"), Vector3.forward, false, 1.18f); FBX2NPRCompare.SetSS(1);
            string line = name + ": linePosition " + rig.linePosition + ", chinTilt " + rig.chinTilt + ", rim " + rig.rim + ", outline " + rig.outline + ", 피처 active " + (feat != null && feat.isActive);
            UnityEngine.Object.DestroyImmediate(go);
            return line;
        }

        static string ShotPaint(string outDir, string name, Action<PainterlyRig> set)
        {
            FBX2NPRRuntimeCheck.NewStage(false);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath("Ch07_Painterly")));
            var rig = go.GetComponentInChildren<PainterlyRig>(true); set(rig);
            var feat = FBX2NPRConverter.FindFeature();
            rig.ApplyInk(); rig.ApplyEyeInk(); if (feat != null && rig.inkLine > 0f) feat.settings.thickness = rig.inkThickness * 2f;
            var notes = new List<string>(); var head = FBX2NPRRuntimeCheck.HeadOf(go, "Ch07", notes);
            FBX2NPRCompare.SetSS(2); FBX2NPRCompare.Shoot(head, 600, 600, Path.Combine(outDir, name + ".png"), Vector3.forward, false, 1.18f); FBX2NPRCompare.SetSS(1);
            string line = name + ": inkLine " + rig.inkLine + ", eyeInk " + rig.eyeInk + ", 잉크 재질 " + rig.inkEntries.Count + ", 피처 active " + (feat != null && feat.isActive);
            UnityEngine.Object.DestroyImmediate(go);
            return line;
        }
    }
}
