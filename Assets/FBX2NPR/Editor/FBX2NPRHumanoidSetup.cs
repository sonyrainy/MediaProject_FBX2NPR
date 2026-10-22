// FBX2NPRHumanoidSetup.cs — 애니메이션 검증 준비 (기록 97번).
// (1) 테스트 모델 4체와 클립(Assets/TestSet/Anims/*.fbx)을 Humanoid 로 임포트 — 세 캐릭터의 본 이름 접두어가 달라(mixamorig8:/mixamorig10:/mixamorig:) Generic 으로는 한 캐릭터에만 붙는다.
//     클립의 기준 아바타는 원래 캐릭터의 T자세 모델에서 복사한다 (ImportClips, 기록 99번 — 클립 파일 뼈대로 만들면 왼발이 꼬였다). 클립만 다시: FixClips
// (2) 변환된 프리팹 8개(툰·회화풍)의 루트 Animator 에 모델의 Humanoid 아바타만 넣는다 — 다시 변환하지 않으므로 텍스처·재질·메시는 그대로다.
// (3) 클립마다 상태가 하나씩 있는 AnimatorController 를 만든다 (Assets/FBX2NPRRuntimeCheck/Clips.controller).
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRHumanoidSetup.Run -OutFolder "NormalTestOut\runtime\anim"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRHumanoidSetup
    {
        public const string AnimDir = "Assets/TestSet/Anims";
        public const string ControllerPath = "Assets/FBX2NPRRuntimeCheck/Clips.controller";
        static readonly string[] Models = { "Ch07", "TheBoss", "Jones", "unitychan" };
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "runtime", "anim"); } }

        public static string ModelPath(string name)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.StartsWith(AnimDir)) continue;
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }
        public static Avatar AvatarOf(string modelPath)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(modelPath)) { var av = o as Avatar; if (av != null) return av; }
            return null;
        }
        public static List<AnimationClip> Clips()
        {
            var list = new List<AnimationClip>();
            if (!AssetDatabase.IsValidFolder(AnimDir)) return list;
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { AnimDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) list.Add(c); }
            }
            list.Sort((a, b) => Order(a.name).CompareTo(Order(b.name)));
            return list;
        }
        static int Order(string n) { n = n.ToLowerInvariant(); return n.Contains("idle") ? 0 : n.Contains("walk") ? 1 : n.Contains("turn") ? 3 : n.Contains("run") ? 2 : 4; }

        /// <summary>
        /// 클립의 기준 아바타를 줄 모델 이름: 클립 파일 이름의 '@' 앞(믹사모 규칙 "캐릭터@동작")과 같은 이름의 모델, 없으면 그 앞부분의 첫 '_' 앞(Ch07_nonPBR → Ch07). 못 찾으면 null.
        /// </summary>
        public static string ClipSourceModel(string clipPath)
        {
            string n = Path.GetFileNameWithoutExtension(clipPath); int at = n.IndexOf('@'); if (at <= 0) return null;
            string head = n.Substring(0, at);
            if (ModelPath(head) != null) return head;
            int us = head.IndexOf('_'); if (us > 0 && ModelPath(head.Substring(0, us)) != null) return head.Substring(0, us);
            return null;
        }

        /// <summary>
        /// 클립을 Humanoid 로 임포트한다. 기준 아바타는 클립 파일의 뼈대로 만들지 않고(CreateFromThisModel) 원래 캐릭터의 T자세 모델 아바타를 복사한다(CopyFromOther).
        /// 믹사모에서 스킨 없이 받은 FBX 는 뼈대의 기준 자세가 T자세가 아니라 애니메이션 한 프레임이라, 그 자세로 아바타를 만들면 기준 자세의 비틀림이 재생에 얹힌다 —
        /// 모든 캐릭터의 왼발이 걷기 110.6°, 달리기 173.9° 꼬였다 (사용자 지적, 기록 98번 진단·99번 적용).
        /// </summary>
        /// <summary>
        /// 반복 구간을 잘라 쓸 클립 (클립 이름 → 원본 프레임 [처음, 끝]). 믹사모 Running 은 30fps 25프레임인데 실제 한 사이클은 21프레임이라 끝과 처음 자세가 달랐다:
        /// 루프 맞춤을 켜면 그 차이를 사이클 전체에 나눠 넣어 골반이 최대 0.36m 내려앉고, 끄면 이음매가 튀었다(보통 프레임의 4.9배). 자세가 가장 비슷한 두 프레임 2·23
        /// (차가 보통 프레임 변화의 0.02배) 사이만 쓰면 원본 대비 골반 높이 차 0.001m, 이음매 1.18배 (FBX2NPRFootDiag.RunLoopRange, 기록 99·100번). 새 클립은 RunSeam 으로 먼저 잴 것.
        /// </summary>
        public static readonly Dictionary<string, Vector2> LoopRanges = new Dictionary<string, Vector2> { { "Running", new Vector2(2f, 23f) } };

        static void ImportClips(List<string> log)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { AnimDir }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g); var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                string srcModel = ClipSourceModel(p); var srcAv = srcModel != null ? AvatarOf(ModelPath(srcModel)) : null;
                if (srcAv == null || !srcAv.isValid || !srcAv.isHuman) { log.Add("클립 " + Path.GetFileName(p) + ": 기준 아바타를 줄 T자세 모델을 찾지 못해 건너뜀 (파일 이름 '캐릭터@동작' 의 캐릭터 모델이 Assets/TestSet 에 Humanoid 로 있어야 한다)"); continue; }
                mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther; mi.sourceAvatar = srcAv; mi.importAnimation = true;
                var clips = mi.defaultClipAnimations;
                var notes = new List<string>();
                for (int i = 0; i < clips.Length; i++)
                {
                    clips[i].loopTime = true; clips[i].loopPose = true; clips[i].lockRootRotation = true;   // loopPose: 끝과 처음을 이어 붙인다 (기록 97번). 끝·처음이 다른 클립은 아래 반복 구간으로 먼저 자른다 (기록 100번)
                    clips[i].keepOriginalOrientation = true;
                    clips[i].lockRootHeightY = true; clips[i].keepOriginalPositionY = true; clips[i].lockRootPositionXZ = false; clips[i].keepOriginalPositionXZ = true;
                    Vector2 range;
                    string fn = Path.GetFileNameWithoutExtension(p); string atName = fn.IndexOf('@') >= 0 ? fn.Substring(fn.IndexOf('@') + 1) : fn;
                    if (LoopRanges.TryGetValue(clips[i].name, out range) || LoopRanges.TryGetValue(atName, out range)) { clips[i].firstFrame = range.x; clips[i].lastFrame = range.y; notes.Add(string.Format("반복 구간 {0}~{1} 프레임", range.x, range.y)); }
                }
                mi.clipAnimations = clips; mi.SaveAndReimport();
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(p)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) log.Add(string.Format("클립 {0} ({1}): 기준 아바타 {2} 복사, 길이 {3:F2}초, 사람형 {4}, 반복 {5}, 평균속도 {6}{7}", c.name, Path.GetFileName(p), srcAv.name, c.length, c.isHumanMotion, c.isLooping, c.averageSpeed, notes.Count > 0 ? ", " + string.Join(", ", notes.ToArray()) : "")); }
            }
        }

        /// <summary>배치: 클립만 다시 임포트하고(모델·프리팹은 그대로) 컨트롤러가 여전히 같은 클립을 가리키는지 확인한다 (기록 99번).</summary>
        public static void FixClips()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            try
            {
                ImportClips(log);
                AssetDatabase.SaveAssets();
                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
                if (ctrl == null) log.Add("컨트롤러 없음: " + ControllerPath);
                else foreach (var st in ctrl.layers[0].stateMachine.states)
                    {
                        var c = st.state.motion as AnimationClip;
                        log.Add(string.Format("컨트롤러 상태 {0}: {1}", st.state.name, c != null ? c.name + " (" + AssetDatabase.GetAssetPath(c) + ", 사람형 " + c.isHumanMotion + ")" : "클립 연결 끊김"));
                    }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_fixclips.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[Humanoid] " + l);
            }
        }

        public static void Run()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            try
            {
                // 1) 모델 → Humanoid
                foreach (var m in Models)
                {
                    string mp = ModelPath(m); if (mp == null) { log.Add("모델 없음: " + m); continue; }
                    var mi = (ModelImporter)AssetImporter.GetAtPath(mp);
                    string before = mi.animationType + "/" + mi.avatarSetup;
                    if (mi.animationType != ModelImporterAnimationType.Human || mi.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
                    { mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; mi.SaveAndReimport(); }
                    var av = AvatarOf(mp);
                    log.Add(string.Format("모델 {0}: {1} → {2}/{3}, 아바타 {4} (유효 {5}, 사람형 {6})", m, before, mi.animationType, mi.avatarSetup, av != null ? av.name : "없음", av != null && av.isValid, av != null && av.isHuman));
                    if (av != null && av.isHuman) { int mapped = 0; foreach (var hb in mi.humanDescription.human) if (!string.IsNullOrEmpty(hb.boneName)) mapped++; log.Add("   매핑된 사람 본 " + mapped + "개"); }
                }
                // 2) 클립 → Humanoid (기준 아바타는 원래 캐릭터의 T자세 모델에서 복사), 반복·회전/높이는 자세에 굽기(제자리), 앞뒤 이동은 루트로 (Animator 의 루트 모션을 꺼서 제자리에 둔다)
                if (AssetDatabase.IsValidFolder(AnimDir)) ImportClips(log);
                else log.Add("클립 폴더 없음: " + AnimDir);
                // 3) 프리팹 8개에 아바타
                foreach (var m in Models)
                {
                    string mp = ModelPath(m); var av = mp != null ? AvatarOf(mp) : null;
                    foreach (var suffix in new[] { "_Toon", "_Painterly" })
                    {
                        string pp = FBX2NPRConverter.OutRoot + "/" + FBX2NPRConverter.Safe(m) + "/" + FBX2NPRConverter.Safe(m) + suffix + ".prefab";
                        if (AssetDatabase.LoadAssetAtPath<GameObject>(pp) == null) { log.Add("프리팹 없음: " + pp); continue; }
                        var root = PrefabUtility.LoadPrefabContents(pp);
                        try
                        {
                            var an = root.GetComponent<Animator>(); if (an == null) an = root.AddComponent<Animator>();
                            an.avatar = av; an.applyRootMotion = false; an.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                            PrefabUtility.SaveAsPrefabAsset(root, pp);
                            log.Add(string.Format("프리팹 {0}: 아바타 {1} 연결 (사람형 {2})", Path.GetFileName(pp), av != null ? av.name : "없음", av != null && av.isHuman));
                        }
                        finally { PrefabUtility.UnloadPrefabContents(root); }
                    }
                }
                // 4) 컨트롤러
                var clipsAll = Clips();
                if (clipsAll.Count > 0)
                {
                    FBX2NPRConverter.EnsureFolder(Path.GetDirectoryName(ControllerPath).Replace('\\', '/'));
                    if (AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) != null) AssetDatabase.DeleteAsset(ControllerPath);
                    var ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
                    var sm = ctrl.layers[0].stateMachine;
                    foreach (var c in clipsAll) { var st = sm.AddState(c.name); st.motion = c; if (sm.defaultState == null) sm.defaultState = st; }
                    log.Add("컨트롤러 " + ControllerPath + ": 상태 " + clipsAll.Count + "개 (" + string.Join(", ", clipsAll.ConvertAll(c => c.name).ToArray()) + ")");
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_humanoid.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[Humanoid] " + l);
            }
        }
    }
}
