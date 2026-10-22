// FBX2NPRFootDiag.cs — 진단 전용 (기록 98번): 걸을 때 왼발이 바깥으로 꼬이는 원인 확인. 실제 클립·프리팹은 건드리지 않는다.
// 가설: 스킨 없이 받은 믹사모 클립은 뼈대의 기준 자세가 T자세가 아니라 애니메이션의 한 프레임이다. 클립마다 '이 모델에서 아바타 생성'을 하면
//       그 자세가 아바타의 기준이 되어, 기준 자세의 발 비틀림이 Humanoid 재생에 그대로 얹힌다.
// 비교 (같은 클립·같은 시각):
//   A 지금 설정    — 클립 임포트 Human + CreateFromThisModel (Assets/TestSet/Anims 의 실제 클립)
//   B 정답         — 같은 FBX 복사본을 Generic 으로 임포트해 Ch07 원본 뼈대에 그대로 재생 (리타깃 없음 = 믹사모가 만든 동작 그대로)
//   C 고친 설정 후보 — 같은 FBX 복사본을 Human + CopyFromOther(Ch07 모델의 T자세 아바타) 로 임포트
// 잰 것: 골반 앞 방향 기준 발끝 방향(발→발가락, 바닥 평면) 각도, 좌우 각각. 다리 부분 렌더.
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRFootDiag.Run -OutFolder "NormalTestOut\runtime\anim\feet"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRFootDiag
    {
        const string DiagDir = "Assets/FBX2NPRDiag";
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "runtime", "anim", "feet"); } }
        static readonly string[] ClipNames = { "Walking", "Running", "Idle", "Running Turn 180" };
        static readonly string[] Models = { "Ch07", "TheBoss", "Jones", "unitychan" };

        public static void Run()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>(); var csv = new List<string> { "clip,variant,model,t,left_yaw_deg,right_yaw_deg" };
            var temps = new List<UnityEngine.Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                FBX2NPRConverter.EnsureFolder(DiagDir);
                var ch07Path = FBX2NPRHumanoidSetup.ModelPath("Ch07"); var ch07Av = FBX2NPRHumanoidSetup.AvatarOf(ch07Path);
                log.Add("Ch07 모델 아바타: " + (ch07Av != null ? ch07Av.name + " (사람형 " + ch07Av.isHuman + ", 유효 " + ch07Av.isValid + ")" : "없음"));
                foreach (var cn in ClipNames)
                {
                    string src = FBX2NPRHumanoidSetup.AnimDir + "/Ch07_nonPBR@" + cn + ".fbx";
                    if (AssetDatabase.LoadMainAssetAtPath(src) == null) { log.Add("클립 없음: " + src); continue; }
                    string safe = cn.Replace(' ', '_');
                    string gPath = DiagDir + "/" + safe + "_generic.fbx", cPath = DiagDir + "/" + safe + "_copyavatar.fbx";
                    foreach (var dst in new[] { gPath, cPath }) if (AssetDatabase.LoadMainAssetAtPath(dst) == null) AssetDatabase.CopyAsset(src, dst);
                    // B: Generic
                    { var mi = (ModelImporter)AssetImporter.GetAtPath(gPath); mi.animationType = ModelImporterAnimationType.Generic; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; mi.importAnimation = true;
                      var cl = mi.defaultClipAnimations; for (int i = 0; i < cl.Length; i++) cl[i].loopTime = true; mi.clipAnimations = cl; mi.SaveAndReimport(); }
                    // C: Human + 모델 아바타 복사 (클립 설정은 FBX2NPRHumanoidSetup 과 같게)
                    { var mi = (ModelImporter)AssetImporter.GetAtPath(cPath); mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther; mi.sourceAvatar = ch07Av; mi.importAnimation = true;
                      var cl = mi.defaultClipAnimations;
                      for (int i = 0; i < cl.Length; i++) { cl[i].loopTime = true; cl[i].loopPose = true; cl[i].lockRootRotation = true; cl[i].keepOriginalOrientation = true; cl[i].lockRootHeightY = true; cl[i].keepOriginalPositionY = true; cl[i].lockRootPositionXZ = false; cl[i].keepOriginalPositionXZ = true; }
                      mi.clipAnimations = cl; mi.SaveAndReimport(); }
                    var clipA = Clip(src); var clipB = Clip(gPath); var clipC = Clip(cPath);
                    log.Add(string.Format("== {0}: A {1} (사람형 {2}), B {3} (사람형 {4}), C {5} (사람형 {6}, 아바타 출처 {7})", cn, clipA != null ? clipA.name : "없음", clipA != null && clipA.isHumanMotion, clipB != null ? clipB.name : "없음", clipB != null && clipB.isHumanMotion,
                        clipC != null ? clipC.name : "없음", clipC != null && clipC.isHumanMotion, ((ModelImporter)AssetImporter.GetAtPath(cPath)).sourceAvatar != null ? ((ModelImporter)AssetImporter.GetAtPath(cPath)).sourceAvatar.name : "없음"));
                    if (clipA == null || clipB == null || clipC == null) continue;
                    var ts = new[] { 0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f };
                    foreach (var m in Models)
                    {
                        string mp = FBX2NPRHumanoidSetup.ModelPath(m); if (mp == null) continue;
                        var variants = m == "Ch07" ? new[] { "A", "B", "C" } : new[] { "A", "C" };   // B(Generic)는 같은 뼈대(Ch07)에만
                        // rel[variant][k][bone] = 골반(Hips) 기준 회전 — 뼈: 0 왼허벅지 1 왼종아리 2 왼발 3 오른허벅지 4 오른종아리 5 오른발
                        var rel = new Dictionary<string, Quaternion[][]>();
                        foreach (var v in variants)
                        {
                            rel[v] = new Quaternion[ts.Length][];
                            for (int k = 0; k < ts.Length; k++)
                            {
                                FBX2NPRRuntimeCheck.NewStage(false);
                                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));
                                var bones = Bones(go);   // 애니메이터가 있을 때 휴머노이드 매핑으로 찾아 둔다 (B 는 아래에서 애니메이터를 지운다)
                                var clip = v == "A" ? clipA : v == "B" ? clipB : clipC;
                                float time = ts[k] * clip.length;
                                var rest = RestOf(bones);
                                if (v == "B") { var an0 = go.GetComponent<Animator>(); if (an0 != null) UnityEngine.Object.DestroyImmediate(an0); clip.SampleAnimation(go, time); }   // Generic: 애니메이터 없이 변환 곡선만
                                else SampleHuman(go, clip, time);
                                if (k == 1) log.Add(string.Format(CultureInfo.InvariantCulture, "    ({0} {1} {2}: 다리 뼈 로컬 회전 변화 합 {3:F1}° — 0 이면 재생이 안 걸린 것)", cn, m, v, PoseChange(bones, rest)));
                                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>()) { smr.enabled = false; smr.enabled = true; }   // 편집 모드: 껐다 켜야 본 변화가 렌더에 반영
                                var q = new Quaternion[6]; var hips = bones[6];
                                for (int bi = 0; bi < 6; bi++) q[bi] = bones[bi] != null && hips != null ? Quaternion.Inverse(hips.rotation) * bones[bi].rotation : Quaternion.identity;
                                rel[v][k] = q;
                                if ((m == "Ch07" || m == "unitychan" || m == "TheBoss") && (cn == "Walking" || cn == "Running") && (k % 2 == 0))
                                {
                                    var b = LegBounds(bones);
                                    FBX2NPRCompare.SetSS(1);
                                    FBX2NPRCompare.Shoot(b, 300, 300, Path.Combine(OutDir, string.Format("{0}_{1}_{2}_t{3}.png", safe, m, v, k)), new Vector3(0f, 0.55f, 1f), false, 1.05f);
                                }
                                UnityEngine.Object.DestroyImmediate(go);
                            }
                        }
                        string[] bn = { "왼허벅지", "왼종아리", "왼발", "오른허벅지", "오른종아리", "오른발" };
                        foreach (var pair in m == "Ch07" ? new[] { new[] { "A", "B" }, new[] { "C", "B" }, new[] { "A", "C" } } : new[] { new[] { "A", "C" } })
                        {
                            var sb = new StringBuilder(string.Format("  {0} {1} 대 {2} (골반 기준 뼈 회전 차이, 8개 시각 평균/최대):", m, pair[0], pair[1]));
                            for (int bi = 0; bi < 6; bi++)
                            {
                                double sum = 0, mx = 0;
                                for (int k = 0; k < ts.Length; k++) { double a = Quaternion.Angle(rel[pair[0]][k][bi], rel[pair[1]][k][bi]); sum += a; mx = Math.Max(mx, a); }
                                sb.Append(string.Format(CultureInfo.InvariantCulture, " {0} {1:F1}/{2:F1}°", bn[bi], sum / ts.Length, mx));
                                csv.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1}_vs_{2},{3},{4},{5:F2},{6:F2}", cn, pair[0], pair[1], m, bi, sum / ts.Length, mx));
                            }
                            log.Add(sb.ToString());
                        }
                    }
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_feet.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                File.WriteAllText(Path.Combine(OutDir, "feet_yaw.csv"), string.Join("\n", csv.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FootDiag] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        /// <summary>
        /// 수정 뒤 확인 (기록 99번): Ch07 에서 클립마다 세 가지를 같은 시각에 비교한다 — B 원본(같은 FBX 를 Generic 으로 원본 뼈대에 재생), O 옛 설정(클립 뼈대로 아바타 생성),
        /// A 지금 설정(실제 클립, Ch07 T자세 아바타 복사). 골반 높이(골반 − 가장 낮은 발), 무릎 굽힘(허벅지·종아리 방향 사이 각), 상체 뼈 회전(골반 기준)을 재고 전신을 찍는다.
        /// "고친 뒤 Idle 이 웅크린 자세가 됐다"는 재검사 지적이 원본 그대로인지 확인하려고 만들었다.
        /// </summary>
        public static void RunPoseCompare()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                FBX2NPRConverter.EnsureFolder(DiagDir);
                string mp = FBX2NPRHumanoidSetup.ModelPath("Ch07");
                foreach (var cn in ClipNames)
                {
                    string src = FBX2NPRHumanoidSetup.AnimDir + "/Ch07_nonPBR@" + cn + ".fbx";
                    if (AssetDatabase.LoadMainAssetAtPath(src) == null) { log.Add("클립 없음: " + src); continue; }
                    string safe = cn.Replace(' ', '_');
                    string gPath = DiagDir + "/" + safe + "_generic.fbx", oPath = DiagDir + "/" + safe + "_old.fbx";
                    foreach (var dst in new[] { gPath, oPath }) if (AssetDatabase.LoadMainAssetAtPath(dst) == null) AssetDatabase.CopyAsset(src, dst);
                    { var mi = (ModelImporter)AssetImporter.GetAtPath(gPath); mi.animationType = ModelImporterAnimationType.Generic; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; mi.importAnimation = true;
                      var cl = mi.defaultClipAnimations; for (int i = 0; i < cl.Length; i++) cl[i].loopTime = true; mi.clipAnimations = cl; mi.SaveAndReimport(); }
                    { var mi = (ModelImporter)AssetImporter.GetAtPath(oPath); mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; mi.importAnimation = true;
                      var cl = mi.defaultClipAnimations;
                      for (int i = 0; i < cl.Length; i++) { cl[i].loopTime = true; cl[i].loopPose = true; cl[i].lockRootRotation = true; cl[i].keepOriginalOrientation = true; cl[i].lockRootHeightY = true; cl[i].keepOriginalPositionY = true; cl[i].lockRootPositionXZ = false; cl[i].keepOriginalPositionXZ = true; }
                      mi.clipAnimations = cl; mi.SaveAndReimport(); }
                    var clips = new Dictionary<string, AnimationClip> { { "B", Clip(gPath) }, { "O", Clip(oPath) }, { "A", Clip(src) } };
                    if (clips["B"] == null || clips["O"] == null || clips["A"] == null) { log.Add(cn + ": 클립을 못 읽음"); continue; }
                    var ts = new[] { 0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f };
                    var pelvis = new Dictionary<string, double[]>(); var knee = new Dictionary<string, double[][]>(); var upper = new Dictionary<string, Quaternion[][]>();
                    foreach (var v in new[] { "B", "O", "A" })
                    {
                        pelvis[v] = new double[ts.Length]; knee[v] = new double[ts.Length][]; upper[v] = new Quaternion[ts.Length][];
                        for (int k = 0; k < ts.Length; k++)
                        {
                            FBX2NPRRuntimeCheck.NewStage(false);
                            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));
                            var b = Bones(go); var ub = UpperBones(go);
                            var clip = clips[v]; float time = ts[k] * clip.length;
                            if (v == "B") { var an0 = go.GetComponent<Animator>(); if (an0 != null) UnityEngine.Object.DestroyImmediate(an0); clip.SampleAnimation(go, time); }
                            else SampleHuman(go, clip, time);
                            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>()) { smr.enabled = false; smr.enabled = true; }
                            float low = float.MaxValue; foreach (int i in new[] { 2, 5, 7, 8 }) if (b[i] != null) low = Mathf.Min(low, b[i].position.y);
                            pelvis[v][k] = b[6] != null ? b[6].position.y - low : 0;
                            knee[v][k] = new double[] { Flex(b[0], b[1], b[2]), Flex(b[3], b[4], b[5]) };
                            var q = new Quaternion[ub.Length]; for (int i = 0; i < ub.Length; i++) q[i] = ub[i] != null && b[6] != null ? Quaternion.Inverse(b[6].rotation) * ub[i].rotation : Quaternion.identity;
                            upper[v][k] = q;
                            if (k % 2 == 0)
                            {
                                var bb = FBX2NPRRuntimeCheck.RendererBounds(go); bb.Expand(new Vector3(0.1f, 0.1f, 0.1f));
                                FBX2NPRCompare.SetSS(1);
                                FBX2NPRCompare.Shoot(bb, 300, 420, Path.Combine(OutDir, string.Format("pose_{0}_{1}_t{2}.png", safe, v, k)), new Vector3(0f, 0.15f, 1f), false, 1.05f);
                            }
                            UnityEngine.Object.DestroyImmediate(go);
                        }
                    }
                    log.Add("== " + cn + " (Ch07, 8개 시각)");
                    foreach (var v in new[] { "B", "O", "A" })
                    {
                        double pAvg = 0, kl = 0, kr = 0; for (int k = 0; k < ts.Length; k++) { pAvg += pelvis[v][k] / ts.Length; kl += knee[v][k][0] / ts.Length; kr += knee[v][k][1] / ts.Length; }
                        log.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: 골반 높이(발 기준) 평균 {1:F3}m, 무릎 굽힘 평균 왼 {2:F1}° 오른 {3:F1}°  [{4}]", v == "B" ? "B 원본" : v == "O" ? "O 옛 설정" : "A 지금 설정", pAvg, kl, kr,
                            string.Join(" ", Array.ConvertAll(pelvis[v], x => x.ToString("F3", CultureInfo.InvariantCulture)))));
                    }
                    string[] un = { "척추", "가슴", "목", "머리", "왼위팔", "오른위팔" };
                    foreach (var v in new[] { "O", "A" })
                    {
                        var sb = new StringBuilder("  " + (v == "O" ? "O 옛 설정" : "A 지금 설정") + " 대 원본, 골반 기준 상체 뼈 회전 차이 평균/최대:");
                        for (int i = 0; i < un.Length; i++) { double s = 0, mx = 0; for (int k = 0; k < ts.Length; k++) { double a = Quaternion.Angle(upper[v][k][i], upper["B"][k][i]); s += a; mx = Math.Max(mx, a); } sb.Append(string.Format(CultureInfo.InvariantCulture, " {0} {1:F1}/{2:F1}°", un[i], s / ts.Length, mx)); }
                        log.Add(sb.ToString());
                    }
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_pose_compare.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FootDiag] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        /// <summary>
        /// 진단 (기록 99번): Running 에서 몸이 옆으로 돌고 골반이 내려앉는 프레임이 있다(옛 설정·지금 설정 모두, 원본에는 없음). 클립 설정을 바꾼 사본으로
        /// 원본(Generic) 대비 골반 방향(바닥 평면 각)과 골반 높이 차이를 16개 시각에서 잰다. 모든 사본은 Ch07 T자세 아바타를 복사한다.
        /// </summary>
        public static void RunRootSettings()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                FBX2NPRConverter.EnsureFolder(DiagDir);
                string mp = FBX2NPRHumanoidSetup.ModelPath("Ch07"); var av = FBX2NPRHumanoidSetup.AvatarOf(mp);
                var envClips = Environment.GetEnvironmentVariable("FBX2NPR_DIAG_CLIPS");
                var clipNames = string.IsNullOrEmpty(envClips) ? new[] { "Running", "Walking" } : envClips.Split(';');
                // (이름, loopPose, lockRootRotation, keepOriginalOrientation, lockRootHeightY, keepOriginalPositionY)
                var sets = new[] {
                    new object[] { "A 지금(루프맞춤O·회전굽기O·원본기준)", true, true, true, true, true },
                    new object[] { "R1 루프맞춤 끔", false, true, true, true, true },
                    new object[] { "R2 회전 기준=몸 방향", true, true, false, true, true },
                    new object[] { "R3 회전 굽기 끔", true, false, true, true, true },
                    new object[] { "R4 루프맞춤 끔+회전 굽기 끔", false, false, true, true, true },
                };
                foreach (var cn in clipNames)
                {
                    string src = FBX2NPRHumanoidSetup.AnimDir + "/Ch07_nonPBR@" + cn + ".fbx"; string safe = cn.Replace(' ', '_');
                    string gPath = DiagDir + "/" + safe + "_generic.fbx";
                    if (AssetDatabase.LoadMainAssetAtPath(gPath) == null) AssetDatabase.CopyAsset(src, gPath);
                    { var mi = (ModelImporter)AssetImporter.GetAtPath(gPath); mi.animationType = ModelImporterAnimationType.Generic; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; mi.importAnimation = true;
                      var cl = mi.defaultClipAnimations; for (int i = 0; i < cl.Length; i++) cl[i].loopTime = true; mi.clipAnimations = cl; mi.SaveAndReimport(); }
                    var clipB = Clip(gPath);
                    const int N = 16;
                    var yawB = new float[N]; var hB = new float[N];
                    for (int k = 0; k < N; k++)
                    {
                        FBX2NPRRuntimeCheck.NewStage(false);
                        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp)); var b = Bones(go);
                        var an0 = go.GetComponent<Animator>(); if (an0 != null) UnityEngine.Object.DestroyImmediate(an0);
                        clipB.SampleAnimation(go, k * clipB.length / N);
                        yawB[k] = PelvisYaw(b); hB[k] = PelvisHeight(b);
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                    log.Add("== " + cn + " 원본 골반 방향(°): " + string.Join(" ", Array.ConvertAll(yawB, x => x.ToString("F0", CultureInfo.InvariantCulture))) + " | 골반 높이: " + string.Join(" ", Array.ConvertAll(hB, x => x.ToString("F2", CultureInfo.InvariantCulture))));
                    for (int si = 0; si < sets.Length; si++)
                    {
                        var s = sets[si]; string path = DiagDir + "/" + safe + "_root" + si + ".fbx";
                        if (AssetDatabase.LoadMainAssetAtPath(path) == null) AssetDatabase.CopyAsset(src, path);
                        var mi = (ModelImporter)AssetImporter.GetAtPath(path); mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther; mi.sourceAvatar = av; mi.importAnimation = true;
                        var cl = mi.defaultClipAnimations;
                        for (int i = 0; i < cl.Length; i++) { cl[i].loopTime = true; cl[i].loopPose = (bool)s[1]; cl[i].lockRootRotation = (bool)s[2]; cl[i].keepOriginalOrientation = (bool)s[3]; cl[i].lockRootHeightY = (bool)s[4]; cl[i].keepOriginalPositionY = (bool)s[5]; cl[i].lockRootPositionXZ = false; cl[i].keepOriginalPositionXZ = true; }
                        mi.clipAnimations = cl; mi.SaveAndReimport();
                        var clip = Clip(path);
                        var dy = new float[N]; var dh = new float[N]; float maxY = 0, maxH = 0, sumY = 0, sumH = 0;
                        for (int k = 0; k < N; k++)
                        {
                            FBX2NPRRuntimeCheck.NewStage(false);
                            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp)); var b = Bones(go);
                            SampleHuman(go, clip, k * clip.length / N);
                            dy[k] = Mathf.DeltaAngle(yawB[k], PelvisYaw(b)); dh[k] = PelvisHeight(b) - hB[k];
                            maxY = Mathf.Max(maxY, Mathf.Abs(dy[k])); maxH = Mathf.Max(maxH, Mathf.Abs(dh[k])); sumY += Mathf.Abs(dy[k]) / N; sumH += Mathf.Abs(dh[k]) / N;
                            if (k % 4 == 3) { var bb = FBX2NPRRuntimeCheck.RendererBounds(go); bb.Expand(0.1f); FBX2NPRCompare.SetSS(1); FBX2NPRCompare.Shoot(bb, 240, 340, Path.Combine(OutDir, string.Format("root_{0}_s{1}_k{2}.png", safe, si, k)), new Vector3(0f, 0.15f, 1f), false, 1.05f); }
                            UnityEngine.Object.DestroyImmediate(go);
                        }
                        log.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: 원본 대비 골반 방향 차 평균 {1:F1}° 최대 {2:F1}°, 골반 높이 차 평균 {3:F3}m 최대 {4:F3}m | 방향 차: {5} | 높이 차: {6}", s[0], sumY, maxY, sumH, maxH,
                            string.Join(" ", Array.ConvertAll(dy, x => x.ToString("F0", CultureInfo.InvariantCulture))), string.Join(" ", Array.ConvertAll(dh, x => x.ToString("F2", CultureInfo.InvariantCulture)))));
                    }
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_root_settings.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FootDiag] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        /// <summary>
        /// 진단 (기록 99번): 루프 맞춤(loopPose)을 끄면 클립 끝→처음 이음매에서 자세가 튀는가. 60fps 로 이음매 앞뒤 프레임과 사이클 전체의 '한 프레임 변화'(다리·상체 뼈 회전 변화 합 + 골반 높이 변화)를 잰다.
        /// 원본(Generic), 루프 맞춤 켬(지금), 루프 맞춤 끔(R1 사본). 사본은 RunRootSettings 가 만든 것을 쓴다.
        /// </summary>
        public static void RunSeam()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            try
            {
                string mp = FBX2NPRHumanoidSetup.ModelPath("Ch07");
                foreach (var cn in ClipNames)
                {
                    string safe = cn.Replace(' ', '_');
                    var vars = new[] { new[] { "원본", DiagDir + "/" + safe + "_generic.fbx" }, new[] { "루프맞춤 켬(지금)", FBX2NPRHumanoidSetup.AnimDir + "/Ch07_nonPBR@" + cn + ".fbx" }, new[] { "루프맞춤 끔", DiagDir + "/" + safe + "_root1.fbx" } };
                    log.Add("== " + cn);
                    foreach (var v in vars)
                    {
                        var clip = Clip(v[1]); if (clip == null) { log.Add("  " + v[0] + ": 클립 없음 " + v[1]); continue; }
                        bool generic = !clip.isHumanMotion; int nf = Mathf.Max(2, Mathf.RoundToInt(clip.length * 60f));
                        // 프레임 0..nf-1 의 자세, 그리고 이음매 (nf-1 → 0)
                        var poses = new List<Quaternion[]>(); var heights = new List<float>();
                        FBX2NPRRuntimeCheck.NewStage(false);
                        var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp)); var b = Bones(go); var ub = UpperBones(go);
                        if (generic) { var an0 = go.GetComponent<Animator>(); if (an0 != null) UnityEngine.Object.DestroyImmediate(an0); }
                        for (int f = 0; f < nf; f++)
                        {
                            float t = f / 60f;
                            if (generic) clip.SampleAnimation(go, t); else SampleHuman(go, clip, t);
                            var q = new Quaternion[12]; for (int i = 0; i < 6; i++) q[i] = b[i] != null && b[6] != null ? Quaternion.Inverse(b[6].rotation) * b[i].rotation : Quaternion.identity;
                            for (int i = 0; i < 6; i++) q[6 + i] = ub[i] != null && b[6] != null ? Quaternion.Inverse(b[6].rotation) * ub[i].rotation : Quaternion.identity;
                            poses.Add(q); heights.Add(PelvisHeight(b));
                        }
                        UnityEngine.Object.DestroyImmediate(go);
                        Func<int, int, double> step = (a, c) => { double s = 0; for (int i = 0; i < 12; i++) s += Quaternion.Angle(poses[a][i], poses[c][i]); return s + 100.0 * Math.Abs(heights[a] - heights[c]); };
                        var steps = new List<double>(); for (int f = 1; f < nf; f++) steps.Add(step(f - 1, f));
                        steps.Sort(); double med = steps[steps.Count / 2], p95 = steps[(int)(steps.Count * 0.95)], mx = steps[steps.Count - 1];
                        double seam = step(nf - 1, 0);
                        log.Add(string.Format(CultureInfo.InvariantCulture, "  {0}: 프레임 {1}개, 한 프레임 변화 중앙값 {2:F1} · 95% {3:F1} · 최대 {4:F1} | 이음매(끝→처음) {5:F1} (= 중앙값의 {6:F1}배)  [단위: 뼈 12개 회전 변화 합(°) + 골반 높이 변화(cm)]",
                            v[0], nf, med, p95, mx, seam, seam / Math.Max(1e-6, med)));
                    }
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_seam.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FootDiag] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        /// <summary>
        /// 진단 (기록 99번): 달리기 원본은 끝과 처음 자세가 달라(이음매 변화가 보통 프레임의 4.9배) 루프 맞춤을 켜면 골반이 최대 0.36m 내려앉고, 끄면 이음매가 튄다.
        /// 원본 안에서 자세가 가장 비슷한 두 프레임(간격은 클립 길이의 70% 이상)을 찾아 그 구간만 반복하게 자른 사본을 만들고, 원본 대비 충실도와 이음매를 잰다.
        /// 환경변수 FBX2NPR_DIAG_CLIPS (기본 Running).
        /// </summary>
        public static void RunLoopRange()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                FBX2NPRConverter.EnsureFolder(DiagDir);
                string mp = FBX2NPRHumanoidSetup.ModelPath("Ch07"); var av = FBX2NPRHumanoidSetup.AvatarOf(mp);
                var envClips = Environment.GetEnvironmentVariable("FBX2NPR_DIAG_CLIPS");
                foreach (var cn in string.IsNullOrEmpty(envClips) ? new[] { "Running" } : envClips.Split(';'))
                {
                    string src = FBX2NPRHumanoidSetup.AnimDir + "/Ch07_nonPBR@" + cn + ".fbx"; string safe = cn.Replace(' ', '_');
                    var clipB = Clip(DiagDir + "/" + safe + "_generic.fbx"); if (clipB == null) { log.Add(cn + ": Generic 사본 없음 (RunRootSettings 먼저)"); continue; }
                    float fr = clipB.frameRate; int N = Mathf.RoundToInt(clipB.length * fr) + 1;
                    // 원본 프레임별 자세 (다리·상체 뼈 회전 + 골반 높이)
                    FBX2NPRRuntimeCheck.NewStage(false);
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp)); var b = Bones(go); var ub = UpperBones(go);
                    { var an0 = go.GetComponent<Animator>(); if (an0 != null) UnityEngine.Object.DestroyImmediate(an0); }
                    var P = new List<Quaternion[]>(); var H = new List<float>();
                    for (int f = 0; f < N; f++) { clipB.SampleAnimation(go, Mathf.Min(clipB.length, f / fr)); P.Add(PoseOf(b, ub)); H.Add(PelvisHeight(b)); }
                    UnityEngine.Object.DestroyImmediate(go);
                    Func<int, int, double> dist = (i, j) => { double s = 0; for (int k = 0; k < 12; k++) s += Quaternion.Angle(P[i][k], P[j][k]); return s + 100.0 * Math.Abs(H[i] - H[j]); };
                    var steps = new List<double>(); for (int f = 1; f < N; f++) steps.Add(dist(f - 1, f)); steps.Sort(); double med = steps[steps.Count / 2];
                    log.Add(string.Format(CultureInfo.InvariantCulture, "== {0}: 원본 {1}fps, 프레임 {2}개 (0~{3}), 한 프레임 변화 중앙값 {4:F1}, 끝(={3})과 처음(0)의 차 {5:F1}", cn, fr, N, N - 1, med, dist(N - 1, 0)));
                    // 반복 구간 후보: 프레임 i 와 j (j-i ≥ 0.7N) 의 자세가 비슷하면 [i, j] 를 반복할 때 이음매(j→i)는 '같은 자세 두 번' 수준이 된다
                    var cands = new List<double[]>();
                    for (int i = 0; i < N; i++) for (int j = i + (int)(0.7 * N); j < N; j++) cands.Add(new double[] { dist(i, j), i, j });
                    cands.Sort((x, y) => x[0].CompareTo(y[0]));
                    for (int c = 0; c < Math.Min(5, cands.Count); c++) log.Add(string.Format(CultureInfo.InvariantCulture, "  후보 {0}: 프레임 {1}~{2} ({3:F2}초), 두 자세 차 {4:F1} (중앙값의 {5:F2}배)", c + 1, cands[c][1], cands[c][2], (cands[c][2] - cands[c][1]) / fr, cands[c][0], cands[c][0] / med));
                    if (cands.Count == 0) continue;
                    int s0 = (int)cands[0][1], e0 = (int)cands[0][2];
                    // 사본: 구간 [s0, e0] 의 마지막 프레임은 처음과 거의 같으므로 [s0, e0] 그대로 쓴다 (Unity 는 lastFrame 에서 firstFrame 으로 돌아간다)
                    foreach (bool lp in new[] { true, false })
                    {
                        string path = DiagDir + "/" + safe + "_range" + (lp ? "_loop" : "_noloop") + ".fbx";
                        if (AssetDatabase.LoadMainAssetAtPath(path) == null) AssetDatabase.CopyAsset(src, path);
                        var mi = (ModelImporter)AssetImporter.GetAtPath(path); mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther; mi.sourceAvatar = av; mi.importAnimation = true;
                        var cl = mi.defaultClipAnimations;
                        for (int i = 0; i < cl.Length; i++)
                        {
                            cl[i].firstFrame = s0; cl[i].lastFrame = e0;
                            cl[i].loopTime = true; cl[i].loopPose = lp; cl[i].lockRootRotation = true; cl[i].keepOriginalOrientation = true; cl[i].lockRootHeightY = true; cl[i].keepOriginalPositionY = true; cl[i].lockRootPositionXZ = false; cl[i].keepOriginalPositionXZ = true;
                        }
                        mi.clipAnimations = cl; mi.SaveAndReimport();
                        var clip = Clip(path);
                        // 충실도: 사본의 시각 t 는 원본의 (s0/fr + t)
                        FBX2NPRRuntimeCheck.NewStage(false);
                        var g2 = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp)); var b2 = Bones(g2); var ub2 = UpperBones(g2);
                        int n2 = Mathf.RoundToInt(clip.length * fr) + 1; var Q = new List<Quaternion[]>(); var H2 = new List<float>();
                        for (int f = 0; f < n2; f++) { SampleHuman(g2, clip, Mathf.Min(clip.length - 1e-4f, f / fr)); Q.Add(PoseOf(b2, ub2)); H2.Add(PelvisHeight(b2)); }
                        UnityEngine.Object.DestroyImmediate(g2);
                        double maxH = 0, sumA = 0; int cnt = 0;
                        for (int f = 0; f < n2 && s0 + f < N; f++) { maxH = Math.Max(maxH, Math.Abs(H2[f] - H[s0 + f])); double a = 0; for (int k = 0; k < 12; k++) a += Quaternion.Angle(Q[f][k], P[s0 + f][k]); sumA += a / 12.0; cnt++; }
                        Func<int, int, double> d2 = (i, j) => { double s = 0; for (int k = 0; k < 12; k++) s += Quaternion.Angle(Q[i][k], Q[j][k]); return s + 100.0 * Math.Abs(H2[i] - H2[j]); };
                        var st2 = new List<double>(); for (int f = 1; f < n2; f++) st2.Add(d2(f - 1, f)); st2.Sort(); double med2 = st2[st2.Count / 2];
                        // 이음매: 마지막 프레임(=e0, 처음과 거의 같은 자세) 바로 앞 → 처음 다음 프레임, 즉 두 프레임 간격으로 본다
                        double seam = n2 >= 3 ? d2(n2 - 2, 1) / 2.0 : 0;
                        log.Add(string.Format(CultureInfo.InvariantCulture, "  사본 {0} [{1}~{2}]: 길이 {3:F2}초, 원본 대비 골반 높이 차 최대 {4:F3}m, 뼈 회전 차 평균 {5:F1}°, 한 프레임 변화 중앙값 {6:F1}, 이음매(끝 앞→처음 다음, 한 프레임당) {7:F1} (= 중앙값의 {8:F2}배)",
                            lp ? "루프맞춤 켬" : "루프맞춤 끔", s0, e0, clip.length, maxH, sumA / Math.Max(1, cnt), med2, seam, seam / Math.Max(1e-6, med2)));
                    }
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_loop_range.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FootDiag] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        /// <summary>
        /// 최종 확인 (기록 100번): 실제 클립 4개(Assets/TestSet/Anims)를 Ch07 에 재생해 원본(같은 FBX 를 Generic 으로)과 비교한다.
        /// 클립이 구간을 잘라 쓰면(firstFrame) 원본 시각 = firstFrame/fps + t. 골반 높이 차 최대, 다리·상체 뼈 12개 회전 차 평균, 60fps 한 프레임 변화 중앙값 대비 이음매(끝→처음).
        /// </summary>
        public static void RunVerifyActual()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                FBX2NPRConverter.EnsureFolder(DiagDir);
                string mp = FBX2NPRHumanoidSetup.ModelPath("Ch07");
                foreach (var cn in ClipNames)
                {
                    string src = FBX2NPRHumanoidSetup.AnimDir + "/Ch07_nonPBR@" + cn + ".fbx"; string safe = cn.Replace(' ', '_');
                    string gPath = DiagDir + "/" + safe + "_generic.fbx";
                    if (AssetDatabase.LoadMainAssetAtPath(gPath) == null) AssetDatabase.CopyAsset(src, gPath);
                    { var gi = (ModelImporter)AssetImporter.GetAtPath(gPath); gi.animationType = ModelImporterAnimationType.Generic; gi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; gi.importAnimation = true;
                      var gcl = gi.defaultClipAnimations; for (int i = 0; i < gcl.Length; i++) gcl[i].loopTime = true; gi.clipAnimations = gcl; gi.SaveAndReimport(); }
                    var clipB = Clip(gPath); var clipA = Clip(src);
                    var mi = (ModelImporter)AssetImporter.GetAtPath(src); var ca = mi.clipAnimations.Length > 0 ? mi.clipAnimations[0] : null;
                    float first = ca != null ? ca.firstFrame : 0f, last = ca != null ? ca.lastFrame : 0f, fr = clipB.frameRate;
                    int nf = Mathf.Max(2, Mathf.RoundToInt(clipA.length * 60f));
                    FBX2NPRRuntimeCheck.NewStage(false);
                    var ga = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp)); var ba = Bones(ga); var uba = UpperBones(ga);
                    var gb = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp)); var bb = Bones(gb); var ubb = UpperBones(gb);
                    { var an0 = gb.GetComponent<Animator>(); if (an0 != null) UnityEngine.Object.DestroyImmediate(an0); }
                    var PA = new List<Quaternion[]>(); var HA = new List<float>(); double maxH = 0, sumAng = 0;
                    for (int f = 0; f < nf; f++)
                    {
                        float t = f / 60f;
                        SampleHuman(ga, clipA, t); var qa = PoseOf(ba, uba); float ha = PelvisHeight(ba);
                        clipB.SampleAnimation(gb, Mathf.Min(clipB.length, first / fr + t)); var qb = PoseOf(bb, ubb); float hb = PelvisHeight(bb);
                        PA.Add(qa); HA.Add(ha); maxH = Math.Max(maxH, Math.Abs(ha - hb)); double s = 0; for (int k = 0; k < 12; k++) s += Quaternion.Angle(qa[k], qb[k]); sumAng += s / 12.0;
                    }
                    UnityEngine.Object.DestroyImmediate(ga); UnityEngine.Object.DestroyImmediate(gb);
                    Func<int, int, double> step = (a, c) => { double s = 0; for (int k = 0; k < 12; k++) s += Quaternion.Angle(PA[a][k], PA[c][k]); return s + 100.0 * Math.Abs(HA[a] - HA[c]); };
                    var steps = new List<double>(); for (int f = 1; f < nf; f++) steps.Add(step(f - 1, f)); steps.Sort(); double med = steps[steps.Count / 2]; double seam = step(nf - 1, 0);
                    log.Add(string.Format(CultureInfo.InvariantCulture, "{0}: 구간 {1}~{2} 프레임({3}fps), 길이 {4:F2}초, 루프 맞춤 {5} | 원본 대비 골반 높이 차 최대 {6:F3}m, 뼈 12개 회전 차 평균 {7:F1}° | 한 프레임 변화 중앙값 {8:F1}, 이음매(끝→처음) {9:F1} (= {10:F2}배)",
                        cn, first, last, fr, clipA.length, ca != null && ca.loopPose ? "켬" : "끔", maxH, sumAng / nf, med, seam, seam / Math.Max(1e-6, med)));
                }
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_verify_actual.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FootDiag] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static Quaternion[] PoseOf(Transform[] b, Transform[] ub)
        {
            var q = new Quaternion[12];
            for (int i = 0; i < 6; i++) q[i] = b[i] != null && b[6] != null ? Quaternion.Inverse(b[6].rotation) * b[i].rotation : Quaternion.identity;
            for (int i = 0; i < 6; i++) q[6 + i] = ub[i] != null && b[6] != null ? Quaternion.Inverse(b[6].rotation) * ub[i].rotation : Quaternion.identity;
            return q;
        }

        /// <summary>골반 앞 방향(오른 허벅지→왼 허벅지 축과 위쪽의 외적)의 바닥 평면 방향각 (월드 +Z 기준)</summary>
        static float PelvisYaw(Transform[] b)
        {
            if (b[0] == null || b[3] == null) return 0f;
            var lat = Vector3.ProjectOnPlane(b[0].position - b[3].position, Vector3.up);
            var fwd = Vector3.Cross(lat, Vector3.up);   // 왼쪽 x 위 → 뒤쪽이므로 뒤집는다
            return Vector3.SignedAngle(Vector3.forward, -fwd, Vector3.up);
        }
        /// <summary>골반 높이: 골반 − 가장 낮은 발·발가락</summary>
        static float PelvisHeight(Transform[] b)
        {
            float low = float.MaxValue; foreach (int i in new[] { 2, 5, 7, 8 }) if (b[i] != null) low = Mathf.Min(low, b[i].position.y);
            return b[6] != null && low < float.MaxValue ? b[6].position.y - low : 0f;
        }

        /// <summary>무릎(또는 팔꿈치) 굽힘: 위 뼈→가운데 뼈 방향과 가운데 뼈→끝 뼈 방향 사이 각 (곧으면 0)</summary>
        static double Flex(Transform a, Transform b, Transform c)
        {
            if (a == null || b == null || c == null) return 0;
            return Vector3.Angle(b.position - a.position, c.position - b.position);
        }
        /// <summary>상체 뼈 (척추, 가슴, 목, 머리, 왼위팔, 오른위팔). 휴머노이드 매핑 우선, 없으면 믹사모 이름</summary>
        static Transform[] UpperBones(GameObject go)
        {
            var an = go.GetComponent<Animator>();
            var hb = new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm };
            var nm = new[] { "Spine", "Spine1", "Neck", "Head", "LeftArm", "RightArm" };
            var o = new Transform[hb.Length];
            for (int i = 0; i < hb.Length; i++) { Transform t = null; if (an != null && an.avatar != null && an.avatar.isHuman) t = an.GetBoneTransform(hb[i]); if (t == null) t = FindSuffix(go.transform, nm[i]); o[i] = t; }
            return o;
        }

        public const string PreviewControllerPath = DiagDir + "/Clips_fixpreview.controller";

        /// <summary>
        /// 미리보기: 고친 설정 후보(C, 모델 아바타 복사)로 임포트한 클립 사본으로 컨트롤러를 만든다. 상태 이름은 실제 컨트롤러와 같게(드라이버가 이름으로 재생).
        /// 실제 클립(Assets/TestSet/Anims)과 실제 컨트롤러는 건드리지 않는다. 런타임 검증 씬은 FBX2NPR_RC_CONTROLLER 로 이 컨트롤러를 쓴다.
        /// </summary>
        public static void MakePreviewController()
        {
            var log = new List<string>();
            try
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(PreviewControllerPath) != null) AssetDatabase.DeleteAsset(PreviewControllerPath);
                var ctrl = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(PreviewControllerPath);
                var sm = ctrl.layers[0].stateMachine;
                foreach (var orig in FBX2NPRHumanoidSetup.Clips())
                {
                    var c = Clip(DiagDir + "/" + orig.name.Replace(' ', '_') + "_copyavatar.fbx");
                    if (c == null) { log.Add("사본 클립 없음: " + orig.name); continue; }
                    var st = sm.AddState(orig.name); st.motion = c; if (sm.defaultState == null) sm.defaultState = st;
                    log.Add(string.Format("상태 {0}: {1} (사람형 {2}, 반복 {3}, 길이 {4:F2}초 / 원래 {5:F2}초)", orig.name, AssetDatabase.GetAssetPath(c), c.isHumanMotion, c.isLooping, c.length, orig.length));
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                File.WriteAllText(Path.Combine(OutDir, "log_preview_controller.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[FootDiag] " + l);
            }
        }

        static AnimationClip Clip(string path)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(path)) { var c = o as AnimationClip; if (c != null && !c.name.StartsWith("__preview__")) return c; }
            return null;
        }

        /// <summary>Humanoid 클립을 플레이어블 그래프로 한 시각에 평가 (런타임 컨트롤러 상태처럼 발 IK 끔)</summary>
        static void SampleHuman(GameObject go, AnimationClip clip, float time)
        {
            var an = go.GetComponent<Animator>(); if (an == null) an = go.AddComponent<Animator>();
            an.applyRootMotion = false; an.cullingMode = AnimatorCullingMode.AlwaysAnimate; an.enabled = true; an.Rebind();   // 편집 모드에서는 화면에 안 보이면 갱신을 건너뛰므로 AlwaysAnimate
            var graph = PlayableGraph.Create("footdiag");
            try
            {
                var output = AnimationPlayableOutput.Create(graph, "out", an);
                var cp = AnimationClipPlayable.Create(graph, clip); cp.SetApplyFootIK(false); cp.SetTime(time); cp.SetSpeed(0);
                output.SetSourcePlayable(cp);
                graph.Evaluate(0f);
            }
            finally { graph.Destroy(); }
        }

        /// <summary>다리 뼈 (0 왼허벅지 1 왼종아리 2 왼발 3 오른허벅지 4 오른종아리 5 오른발 6 골반 7 왼발가락 8 오른발가락). 휴머노이드 매핑 우선, 없으면 믹사모 이름</summary>
        static Transform[] Bones(GameObject go)
        {
            var an = go.GetComponent<Animator>();
            var hb = new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, HumanBodyBones.Hips, HumanBodyBones.LeftToes, HumanBodyBones.RightToes };
            var nm = new[] { "LeftUpLeg", "LeftLeg", "LeftFoot", "RightUpLeg", "RightLeg", "RightFoot", "Hips", "LeftToeBase", "RightToeBase" };
            var o = new Transform[hb.Length];
            for (int i = 0; i < hb.Length; i++) { Transform t = null; if (an != null && an.avatar != null && an.avatar.isHuman) t = an.GetBoneTransform(hb[i]); if (t == null) t = FindSuffix(go.transform, nm[i]); o[i] = t; }
            return o;
        }
        /// <summary>자세가 실제로 바뀌었나 (진단의 진단): 다리 뼈 6개의 로컬 회전이 기준과 다른 정도(도)의 합</summary>
        static float PoseChange(Transform[] b, Quaternion[] rest)
        {
            float s = 0f; for (int i = 0; i < 6; i++) if (b[i] != null) s += Quaternion.Angle(b[i].localRotation, rest[i]);
            return s;
        }
        static Quaternion[] RestOf(Transform[] b) { var o = new Quaternion[6]; for (int i = 0; i < 6; i++) o[i] = b[i] != null ? b[i].localRotation : Quaternion.identity; return o; }

        static Transform FindSuffix(Transform root, string suffix)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) { var n = t.name; int c = n.LastIndexOf(':'); if ((c >= 0 ? n.Substring(c + 1) : n) == suffix) return t; }
            return null;
        }

        /// <summary>골반 앞 방향(오른 허벅지→왼 허벅지 축과 위쪽의 외적) 기준, 발→발가락 방향의 바닥 평면 각도. 바깥쪽(왼발은 왼쪽, 오른발은 오른쪽)이 +.</summary>
        static bool FootYaw(GameObject go, out float left, out float right)
        {
            left = right = 0f;
            var lul = FindSuffix(go.transform, "LeftUpLeg"); var rul = FindSuffix(go.transform, "RightUpLeg");
            var lf = FindSuffix(go.transform, "LeftFoot"); var rf = FindSuffix(go.transform, "RightFoot");
            var lt = FindSuffix(go.transform, "LeftToeBase"); var rt = FindSuffix(go.transform, "RightToeBase");
            if (lul == null || rul == null || lf == null || rf == null || lt == null || rt == null) return false;
            var lat = Vector3.ProjectOnPlane(lul.position - rul.position, Vector3.up).normalized;   // 캐릭터의 왼쪽
            var fwd = Vector3.Cross(lat, Vector3.up).normalized;                                     // 왼쪽 x 위 = 앞? 부호는 아래에서 확인
            if (Vector3.Dot(fwd, go.transform.forward) < 0f) fwd = -fwd;
            var dl = Vector3.ProjectOnPlane(lt.position - lf.position, Vector3.up); var dr = Vector3.ProjectOnPlane(rt.position - rf.position, Vector3.up);
            left = Vector3.SignedAngle(fwd, dl, Vector3.up); right = Vector3.SignedAngle(fwd, dr, Vector3.up);
            // 위에서 볼 때 시계 방향(+) — 왼발은 반시계(왼쪽)가 바깥이므로 부호를 뒤집는다
            if (Vector3.SignedAngle(fwd, lat, Vector3.up) < 0f) left = -left; else right = -right;
            return true;
        }

        static Bounds LegBounds(Transform[] bones)
        {
            var b = new Bounds(); bool init = false;
            foreach (var t in bones) { if (t == null) continue; if (!init) { b = new Bounds(t.position, Vector3.zero); init = true; } else b.Encapsulate(t.position); }
            b.Expand(new Vector3(0.30f, 0.15f, 0.30f));
            return b;
        }
    }
}
