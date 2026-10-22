using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 변환 전후 비교 렌더. 테스트 캐릭터를 FBX2NPRConverter 로 변환한 뒤, 같은 씬·같은 디렉셔널 라이트에서
    /// 원본(표준 Lit 재질)과 변환한 프리팹을 얼굴·전신으로 찍는다.
    ///
    /// 조건: 디렉셔널 라이트 하나(세기 1, 흰색) + 균일한 주변광(리니어 0.30), 반사 없음. 빛 방향 둘(방위 35도 = 화면 왼쪽에서, -50도 = 화면 오른쪽에서, 고도 25도).
    /// 툰 셰이더는 빛의 '방향'만 쓴다 (세기·색·주변광은 받지 않는다). 변환한 쪽은 화면공간 외곽선을 켜고 원본 쪽은 끈다(외곽선은 변환 결과의 일부다).
    ///
    /// 원본 쪽은 임포트된 재질에서 '임포트 설정 탓에 깨져 보이는 것'만 사본에서 바로잡는다 (에셋은 건드리지 않는다):
    ///   - 노멀맵이 일반 텍스처로 임포트된 경우: NormalMap 타입 사본을 만들어 물린다 (그대로 두면 노멀이 한쪽으로 기울어 옷이 정중선에서 갈라져 보인다)
    ///   - 알파가 있는 텍스처(머리카락·속눈썹 카드): 알파 컷아웃과 양면
    ///   - FBX 의 텍스처 연결이 끊긴 재질(유니티짱): 변환 도구와 같은 규칙으로 찾은 텍스처를 물린다
    ///   - 변환 도구가 빼는 데칼 메시: 원본에서도 끈다
    ///   - 광택 맵(이름이 _Diffuse → _Glossiness / _Specular 인 텍스처)이 따로 있는데 연결되지 않은 경우: 합쳐서 스페큘러 워크플로로 물린다.
    ///     그대로 두면 매끈함 0.5 가 전체에 균일하게 걸려 무광 천(Ch07 정장)이 젖은 가죽처럼 번들거린다.
    /// 맞추지 않은 차이: 툰 셰이더는 양면(Cull Off)이고 원본 Lit 은 뒷면을 그리지 않는다. 한쪽 면만 있는 리본·머리카락은 고개를 돌렸을 때 실루엣이 조금 다르다.
    /// 광택 맵이 없는 재질의 매끈함·스페큘러 값은 임포트된 그대로다 (믹사모: 매끈함 0.5, 스페큘러 0.2).
    ///
    /// 그 밖에 찍는 것: 고개 돌림(머리 본 35도), 팔을 내린 자세, 바닥과 그림자, 프리팹 배율 1.5, 거리별 얼굴(안쪽 선이 뭉치는지), 다시 변환했을 때 식별자 유지.
    /// 결과: NormalTestOut/convert/ 의 PNG 와 log_convert.txt
    /// </summary>
    public static class FBX2NPRCompare
    {
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const string FixDir = FBX2NPRConverter.OutRoot + "/_CompareOriginalFix";
        const float FovY = 24f;
        const float AmbientLinear = 0.30f;
        // 보여 주는 용도의 샷은 2배 크기로 찍어 줄인다(계단 현상 제거). 화면공간 외곽선은 픽셀 단위라서, 같은 모습이 되도록 선 두께도 같은 배수로 키운다
        // (리그는 피처의 실제 두께를 읽어 안쪽 선 페이드 기준을 맞춘다). 픽셀 크기 자체를 재는 시험(거리별 얼굴)은 1배로 찍는다.
        static int SS = 1;
        public static void SetSS(int v) { SS = Mathf.Max(1, v); }   // 다른 시험 스크립트가 같은 촬영 규칙으로 찍을 때
        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Console.WriteLine("[convert] " + m); }
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "convert"); } }

        [MenuItem("FBX2NPR/툰 캐릭터로 변환 + 전후 비교 렌더 (테스트 캐릭터 전부)")]
        public static void Run()
        {
            // 메뉴에서 실행하면 열려 있는 씬을 빈 씬으로 바꾼다. 저장하지 않은 작업이 있으면 먼저 묻는다.
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Log.Clear(); SS = 1;
            Directory.CreateDirectory(OutDir);
            var feat = FBX2NPRConverter.FindFeature();
            bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold, feat.settings.debug } : null;
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
                models.Sort();
                FBX2NPRConverter.DebugDir = OutDir;

                // 1) 변환
                var reports = new List<FBX2NPRConverter.Report>();
                foreach (var m in models)
                {
                    L(""); L("==================== 변환: " + Path.GetFileNameWithoutExtension(m));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var r = FBX2NPRConverter.Convert(m);
                    foreach (var line in r.lines) L("  " + line);
                    L(string.Format(CultureInfo.InvariantCulture, "  걸린 시간 {0:F1}초", sw.Elapsed.TotalSeconds));
                    reports.Add(r);
                }
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

                // 1b) 다시 변환해도 프리팹·메시·재질의 식별자가 그대로인가 (씬에 놓은 프리팹의 연결이 끊기지 않는지)
                L(""); L("==================== 다시 변환 검사");
                for (int i = 0; i < reports.Count; i++)
                {
                    if (reports[i].prefabPath == null) continue;
                    string c = Path.GetFileNameWithoutExtension(reports[i].model);
                    string before = Ids(reports[i].prefabPath);
                    var r2 = FBX2NPRConverter.Convert(reports[i].model);
                    AssetDatabase.SaveAssets();
                    string after = r2.prefabPath != null ? Ids(r2.prefabPath) : "변환 실패";
                    int folderMesh = AssetDatabase.FindAssets("t:Mesh", new[] { Path.GetDirectoryName(r2.prefabPath ?? reports[i].prefabPath).Replace('\\', '/') + "/Meshes" }).Length;
                    int folderMat = AssetDatabase.FindAssets("t:Material", new[] { Path.GetDirectoryName(r2.prefabPath ?? reports[i].prefabPath).Replace('\\', '/') + "/Materials" }).Length;
                    L("  " + c + ": " + (before == after ? "프리팹·리그·렌더러·메시·재질 식별자 모두 유지" : "식별자가 달라짐") + string.Format(" (식별자 {0}개, 폴더의 메시 {1}개, 재질 {2}개)", before.Split('|').Length, folderMesh, folderMat));
                    if (before != after) { L("    전: " + before); L("    후: " + after); }
                    if (r2.prefabPath != null) reports[i] = r2;
                }
                AssetDatabase.Refresh();

                // 2) 비교 렌더
                foreach (var r in reports)
                {
                    if (r.prefabPath == null) continue;
                    string c = Path.GetFileNameWithoutExtension(r.model);
                    L(""); L("==================== 비교 렌더: " + c);
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = new Color(AmbientLinear, AmbientLinear, AmbientLinear, 1f).gamma;   // 색 입력은 감마 값
                    RenderSettings.reflectionIntensity = 0f;
                    var sunGo = new GameObject("Sun"); var sun = sunGo.AddComponent<Light>();
                    sun.type = LightType.Directional; sun.intensity = 1f; sun.color = Color.white; sun.shadows = LightShadows.None;
                    RenderSettings.sun = sun;
                    L(string.Format(CultureInfo.InvariantCulture, "  조명: 디렉셔널 1.0 (흰색), 주변광 프로브 상수항 {0:F3} (리니어, 목표 {1:F2}), 반사 0", RenderSettings.ambientProbe[0, 0], AmbientLinear));

                    var imp = AssetImporter.GetAtPath(r.model);
                    var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
                    var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(r.model));
                    var toon = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(r.prefabPath));
                    foreach (var note in FixOriginal(pbr, crit, r.model, temps)) L("  " + note);
                    var rig = toon.GetComponentInChildren<ToonFaceRig>(true);

                    // 뼈대가 살아 있는지 기록
                    int smrN = 0, boneN = 0, bindN = 0, weightN = 0;
                    foreach (var smr in toon.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (smr.sharedMesh == null) continue;
                        smrN++; boneN += smr.bones.Length; bindN += smr.sharedMesh.bindposes.Length; weightN += smr.sharedMesh.boneWeights.Length;
                    }
                    L(string.Format("  변환한 프리팹: 스킨 메시 {0}개, 본 {1}, 바인드 포즈 {2}, 정점 가중치 {3}, 리그 부모 '{4}', 얼굴 렌더러 {5}, 그 밖 {6}, Animator {7}",
                        smrN, boneN, bindN, weightN, rig != null && rig.transform.parent != null ? rig.transform.parent.name : "-",
                        rig != null ? rig.faceRenderers.Count : 0, rig != null ? rig.otherRenderers.Count : 0, toon.GetComponent<Animator>() != null ? "있음" : "없음"));

                    Action<bool> show = isToon =>
                    {
                        pbr.SetActive(!isToon); toon.SetActive(isToon);
                        MipBias(isToon ? toon : pbr, Mathf.Log(SS, 2f));
                        if (isToon && rig != null)
                        {
                            rig.ApplyOutline();
                            if (feat != null) feat.settings.thickness = rig.outlineThickness * SS;
                            rig.Apply();
                        }
                        if (feat != null) { feat.SetActive(isToon); Dirty(); }
                    };
                    Action<string, Bounds, int, int> pair = (suffix, bb, w, h) =>
                    {
                        show(false); Shoot(bb, w, h, Path.Combine(OutDir, c + "_pbr_" + suffix + ".png"), Vector3.forward, false, 1.18f);
                        show(true); Shoot(bb, w, h, Path.Combine(OutDir, c + "_toon_" + suffix + ".png"), Vector3.forward, false, 1.18f);
                    };
                    SS = 2;
                    // 전신 구도는 바운드의 가로세로 비에 맞춘다 (T 포즈는 가로가 길다)
                    int bh = 900, bw = Mathf.RoundToInt(bh * Mathf.Clamp(r.body.size.x / Mathf.Max(r.body.size.y, 1e-3f), 0.55f, 1.4f) / 4f) * 4;
                    float[] azs = { 35f, -50f }; string[] azName = { "L35", "Lm50" };
                    for (int k = 0; k < azs.Length; k++)
                    {
                        sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.LightDir(azs[k], 25f), Vector3.up);
                        pair("face_" + azName[k], r.head, 700, 700);
                        pair("body_" + azName[k], r.body, bw, bh);
                    }
                    sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.LightDir(35f, 25f), Vector3.up);

                    // 거리별 얼굴: 얼굴이 화면에서 작아질 때 안쪽 선이 덩어리로 뭉치지 않는지 (변환 쪽만). 픽셀 크기를 재는 시험이라 1배로 찍는다.
                    SS = 1; show(true);
                    Shoot(r.head, 700, 700, Path.Combine(OutDir, c + "_toon_face_size700.png"), Vector3.forward, false, 1.18f);
                    foreach (int sz in new[] { 560, 450, 350, 250, 175 })
                        Shoot(r.head, sz, sz, Path.Combine(OutDir, c + "_toon_face_size" + sz + ".png"), Vector3.forward, false, 1.18f);

                    var hp = FBX2NPRConverter.FindHeadBone(pbr.transform, crit.headBoneContains);
                    var ht = FBX2NPRConverter.FindHeadBone(toon.transform, crit.headBoneContains);

                    // 기준점이 실제로 그려지는 얼굴 위에 있는가: 스킨 메시를 지금 자세로 구워(BakeMesh) 코끝 표식에서 가장 가까운 정점까지의 거리를 잰다.
                    // (변환 도구는 메시 좌표를 머리 본 기준 행렬로 월드에 옮긴다. 그 계산이 실제 스키닝 결과와 맞는지의 검증)
                    if (rig != null && rig.noseTip != null)
                    {
                        float best = float.MaxValue; string bestName = "-";
                        foreach (var fr in rig.faceRenderers)
                        {
                            var fsmr = fr as SkinnedMeshRenderer; var fmf = fr != null ? fr.GetComponent<MeshFilter>() : null;
                            Vector3[] wv = null;
                            if (fsmr != null) { var bm = new Mesh(); fsmr.BakeMesh(bm, true); var bv = bm.vertices; wv = new Vector3[bv.Length]; for (int i = 0; i < bv.Length; i++) wv[i] = fsmr.transform.TransformPoint(bv[i]); UnityEngine.Object.DestroyImmediate(bm); }
                            else if (fmf != null && fmf.sharedMesh != null) { var bv = fmf.sharedMesh.vertices; wv = new Vector3[bv.Length]; for (int i = 0; i < bv.Length; i++) wv[i] = fmf.transform.TransformPoint(bv[i]); }
                            if (wv == null) continue;
                            foreach (var p in wv) { float d = (p - rig.noseTip.position).magnitude; if (d < best) { best = d; bestName = fr.name; } }
                        }
                        L(string.Format(CultureInfo.InvariantCulture, "  기준점 검증: 코끝 표식에서 구운 얼굴 메시의 가장 가까운 정점까지 {0:F2}mm ({1})", best * 1000f, bestName));
                    }

                    SS = 2;
                    // 프리팹 배율 1.5: 코 음영·턱선이 얼굴과 같이 커지는지 (같은 구도가 되도록 바운드도 1.5배).
                    // 편집 모드에서 직접 렌더할 때는 스킨 메시가 본 변화를 바로 반영하지 않을 수 있어, 껐다 켜서 다시 계산하게 한다.
                    {
                        Vector3 s0 = toon.transform.localScale; float hy0 = ht != null ? ht.position.y : 0f;
                        toon.SetActive(false); toon.transform.localScale = s0 * 1.5f; show(true);
                        L(string.Format(CultureInfo.InvariantCulture, "  배율 1.5: 머리 본 높이 {0:F3} -> {1:F3} (기대 {2:F3}), 리그 배율 {3:F3}",
                            hy0, ht != null ? ht.position.y : 0f, hy0 * 1.5f, rig != null ? rig.transform.lossyScale.y : 0f));
                        Shoot(new Bounds(r.head.center * 1.5f, r.head.size * 1.5f), 700, 700, Path.Combine(OutDir, c + "_toon_face_scale150.png"), Vector3.forward, false, 1.18f);
                        // 대조: 배율 반영을 끄고(리그의 시험용 스위치) 같은 구도로 한 장. 이 그림이 달라야 위 시험이 의미가 있다.
                        if (rig != null)
                        {
                            rig.ignoreInstanceScale = true; rig.Apply();
                            Shoot(new Bounds(r.head.center * 1.5f, r.head.size * 1.5f), 700, 700, Path.Combine(OutDir, c + "_toon_face_scale150_control.png"), Vector3.forward, false, 1.18f);
                            rig.ignoreInstanceScale = false; rig.Apply();
                        }
                        toon.SetActive(false); toon.transform.localScale = s0; toon.SetActive(true);
                    }

                    // 고개 돌림 (빛은 방위 35도)
                    if (hp != null && ht != null)
                    {
                        hp.Rotate(Vector3.up, 35f, Space.World); ht.Rotate(Vector3.up, 35f, Space.World);
                        pair("face_turn", r.head, 700, 700);
                        hp.Rotate(Vector3.up, -35f, Space.World); ht.Rotate(Vector3.up, -35f, Space.World);
                        L("  고개 돌림 35도: 머리 본 '" + ht.name + "'");
                    }
                    else L("  고개 돌림 생략: 머리 본을 못 찾음");

                    // 자세: 팔을 내리고 고개를 조금 돌린다. 뼈대가 살아 있고 옷·몸의 명암이 자세를 따라가는지.
                    int moved = Pose(pbr.transform, crit, 60f, 20f); Pose(toon.transform, crit, 60f, 20f);
                    L("  자세: 팔 60도 내림 + 고개 20도 (움직인 본 " + moved + "개)");
                    pair("body_pose", r.body, bw, bh);

                    // 바닥과 그림자: 변환한 캐릭터가 바닥에 그림자를 드리우는지 (툰 셰이더의 그림자 패스)
                    var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    ground.transform.position = new Vector3(r.body.center.x, r.body.min.y, r.body.center.z); ground.transform.localScale = Vector3.one * 3f;
                    var gm = new Material(Shader.Find("Universal Render Pipeline/Lit")); temps.Add(gm);
                    gm.SetColor("_BaseColor", new Color(0.55f, 0.55f, 0.57f)); gm.SetFloat("_Smoothness", 0f);
                    ground.GetComponent<Renderer>().sharedMaterial = gm;
                    sun.shadows = LightShadows.Soft;
                    sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.LightDir(35f, 50f), Vector3.up);
                    var view = new Vector3(0.45f, 0.42f, 1f).normalized;
                    show(false); Shoot(r.body, 760, 760, Path.Combine(OutDir, c + "_pbr_ground.png"), view, true, 1.25f);
                    show(true); Shoot(r.body, 760, 760, Path.Combine(OutDir, c + "_toon_ground.png"), view, true, 1.25f);
                    sun.shadows = LightShadows.None;
                    UnityEngine.Object.DestroyImmediate(ground);

                    // 클립이 있으면 변환한 프리팹에 본 경로로 입혀 본다 (Animator 없이 표본만). 지금 팔을 내린 자세이므로,
                    // 클립(믹사모는 T 포즈 한 프레임)이 본에 연결되면 팔이 클립의 자세로 돌아간다.
                    AnimationClip clip = null;
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(r.model))
                    {
                        var cl = o as AnimationClip; if (cl == null || cl.name.StartsWith("__preview__")) continue;
                        L(string.Format(CultureInfo.InvariantCulture, "  클립 '{0}': 길이 {1:F2}초, {2:F0}fps", cl.name, cl.length, cl.frameRate));
                        if (clip == null || cl.length > clip.length) clip = cl;
                    }
                    if (clip == null) L("  클립 없음 (모델 파일에 애니메이션이 들어 있지 않음): 클립 연결은 시험하지 못함");
                    else
                    {
                        Transform arm = null;
                        foreach (var tr in toon.GetComponentsInChildren<Transform>(true)) if (tr.name.ToLowerInvariant().EndsWith("leftarm")) { arm = tr; break; }
                        toon.SetActive(true);
                        Quaternion posed = arm != null ? arm.localRotation : Quaternion.identity;
                        clip.SampleAnimation(toon, 0f);
                        float moved2 = arm != null ? Quaternion.Angle(posed, arm.localRotation) : 0f;
                        L(string.Format(CultureInfo.InvariantCulture, "  클립 표본: 변환한 프리팹에 '{0}' 을 직접 입히니(SampleAnimation) 내려 둔 팔 본이 {1:F1}도 돌아감 ({2})", clip.name, moved2,
                            moved2 > 30f ? "팔 본의 경로가 변환한 프리팹에 연결됨. Animator 재생과 움직이는 클립은 시험하지 않음" : "연결을 확인하지 못함"));
                    }

                    if (feat != null) { feat.SetActive(false); Dirty(); }
                    RestoreMipBias();
                    UnityEngine.Object.DestroyImmediate(pbr); UnityEngine.Object.DestroyImmediate(toon); UnityEngine.Object.DestroyImmediate(sunGo);
                }
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                RestoreMipBias();
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                if (feat != null && keep != null)
                {
                    // 외곽선 피처를 시작할 때 상태로 되돌린다 (다른 실험이 같은 세션에서 이어질 수 있다)
                    feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2];
                    feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.settings.debug = keep[5];
                    feat.SetActive(featWas); Dirty();
                }
                FBX2NPRConverter.DebugDir = null;
            }
            File.WriteAllText(Path.Combine(OutDir, "log_convert.txt"), Log.ToString(), new UTF8Encoding(true));
        }

        /// <summary>
        /// 원본 쪽 보정. 임포트 설정 탓에 깨져 보이는 것만 재질 사본에서 바로잡는다. 그대로 두면 원본이 실제보다 나쁘게 보여 비교가 불공정하다.
        /// </summary>
        public static List<string> FixOriginal(GameObject pbr, FBX2NPRAssetSettings crit, string modelPath, List<UnityEngine.Object> temps)
        {
            var copyOf = new Dictionary<Material, Material>();
            var nAlpha = new List<string>(); var nNormal = new List<string>(); var nTex = new List<string>(); var nDecal = new List<string>(); var nPlain = new List<string>(); var nGloss = new List<string>(); int twoSided = 0;
            foreach (var rend in pbr.GetComponentsInChildren<Renderer>(true))
            {
                var smr = rend as SkinnedMeshRenderer; var mf = rend.GetComponent<MeshFilter>();
                var mesh = smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);
                if (mesh != null && crit.IsIgnoredMesh(mesh.name)) { rend.enabled = false; Add(nDecal, mesh.name); continue; }
                var ms = rend.sharedMaterials; bool changed = false;
                for (int mi = 0; mi < ms.Length; mi++)
                {
                    var m0 = ms[mi]; if (m0 == null) continue;
                    Material mc;
                    if (!copyOf.TryGetValue(m0, out mc))
                    {
                        var c = new Material(m0); bool touched = false;
                        Texture baseTex = m0.mainTexture;
                        if (baseTex == null)
                        {
                            var t = FBX2NPRConverter.ResolveAlbedo(m0, modelPath, crit);
                            if (t != null) { c.mainTexture = t; baseTex = t; touched = true; Add(nTex, m0.name + "=" + t.name); }
                            else Add(nPlain, m0.name);
                        }
                        if (baseTex != null && c.HasProperty("_AlphaClip"))
                        {
                            var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(baseTex)) as TextureImporter;
                            if (ti != null && ti.DoesSourceTextureHaveAlpha())
                            {
                                c.SetFloat("_AlphaClip", 1f); c.SetFloat("_Cutoff", 0.5f); c.EnableKeyword("_ALPHATEST_ON");
                                if (c.HasProperty("_Cull")) c.SetFloat("_Cull", 0f);
                                c.renderQueue = (int)RenderQueue.AlphaTest; c.SetOverrideTag("RenderType", "TransparentCutout");
                                touched = true; Add(nAlpha, m0.name);
                            }
                        }
                        if (c.HasProperty("_BumpMap"))
                        {
                            var bt = c.GetTexture("_BumpMap");
                            var ti = bt != null ? AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(bt)) as TextureImporter : null;
                            if (ti != null && ti.textureType != TextureImporterType.NormalMap)
                            {
                                var fixedTex = NormalCopy(bt);
                                if (fixedTex != null) { c.SetTexture("_BumpMap", fixedTex); c.EnableKeyword("_NORMALMAP"); touched = true; Add(nNormal, bt.name); }
                            }
                        }
                        if (baseTex != null && c.HasProperty("_SpecGlossMap") && c.GetTexture("_SpecGlossMap") == null && (!c.HasProperty("_MetallicGlossMap") || c.GetTexture("_MetallicGlossMap") == null))
                        {
                            string usedNames; var sg = SpecGlossFor(baseTex, temps, out usedNames);
                            if (sg != null)
                            {
                                c.SetFloat("_WorkflowMode", 0f); c.EnableKeyword("_SPECULAR_SETUP");
                                c.SetTexture("_SpecGlossMap", sg); c.EnableKeyword("_METALLICSPECGLOSSMAP");
                                c.SetFloat("_Smoothness", 1f); c.SetFloat("_SmoothnessTextureChannel", 0f);
                                touched = true; Add(nGloss, m0.name + "=" + usedNames);
                            }
                        }
                        // 원본을 전부 양면으로 맞추지는 않는다: Lit 은 뒷면 노멀을 뒤집지 않아 머리카락 안쪽이 검은 띠로 찍혀 원본이 실제보다 나빠 보였다(유니티짱).
                        if (touched) { temps.Add(c); mc = c; } else { UnityEngine.Object.DestroyImmediate(c); mc = m0; }
                        copyOf[m0] = mc;
                    }
                    if (mc != m0) { ms[mi] = mc; changed = true; }
                }
                if (changed) rend.sharedMaterials = ms;
            }
            var notes = new List<string>();
            notes.Add("원본 쪽: 임포트된 Lit 재질. 사본에서 바로잡은 것 —"
                + (nNormal.Count > 0 ? " 노멀맵 타입(일반 텍스처로 임포트돼 있었음): " + string.Join(", ", nNormal.ToArray()) + ";" : "")
                + (nAlpha.Count > 0 ? " 알파 컷아웃·양면: " + string.Join(", ", nAlpha.ToArray()) + ";" : "")
                + (nTex.Count > 0 ? " 끊긴 텍스처 연결: " + string.Join(", ", nTex.ToArray()) + ";" : "")
                + (nDecal.Count > 0 ? " 끈 데칼 메시(변환 쪽과 같게): " + string.Join(", ", nDecal.ToArray()) + ";" : "")
                + (nGloss.Count > 0 ? " 광택 맵 연결(스페큘러 RGB + 광택 A): " + string.Join(", ", nGloss.ToArray()) + ";" : "")
                + (twoSided > 0 ? " 양면 렌더로 맞춘 재질 " + twoSided + "개(툰 쪽과 같게);" : "")
                + (nNormal.Count + nAlpha.Count + nTex.Count + nDecal.Count + nGloss.Count + twoSided == 0 ? " 없음" : ""));
            if (nPlain.Count > 0) notes.Add("원본 쪽: 텍스처 없는 재질(흰색으로 찍힘): " + string.Join(", ", nPlain.ToArray()));
            return notes;
        }
        static void Add(List<string> l, string s) { if (!l.Contains(s)) l.Add(s); }

        /// <summary>
        /// 알베도 옆에 광택 맵(_Glossiness)이 따로 있으면, 스페큘러 맵(_Specular, 없으면 임포트 기본값 0.2)과 합쳐
        /// URP Lit 의 스페큘러 워크플로용 텍스처(RGB 스페큘러 색, A 매끈함)를 만든다. 광택 맵이 없으면 null (임포트 값 그대로 둔다).
        /// </summary>
        static Texture2D SpecGlossFor(Texture baseTex, List<UnityEngine.Object> temps, out string usedNames)
        {
            usedNames = null;
            string bp = AssetDatabase.GetAssetPath(baseTex); if (string.IsNullOrEmpty(bp)) return null;
            Texture2D gloss = Sibling(bp, "Glossiness"), spec = Sibling(bp, "Specular");
            if (gloss == null) return null;
            int w = gloss.width, h = gloss.height;
            var g = ReadBytes(gloss, w, h); var s = spec != null ? ReadBytes(spec, w, h) : null;
            var px = new Color32[w * h];
            byte dflt = (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(0.2f) * 255f);   // 임포트된 재질의 스페큘러 0.2 (리니어) 를 sRGB 값으로
            for (int i = 0; i < px.Length; i++)
                px[i] = s != null ? new Color32(s[i].r, s[i].g, s[i].b, g[i].r) : new Color32(dflt, dflt, dflt, g[i].r);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, true, false);   // RGB 는 색(sRGB), A 는 값 그대로
            t.SetPixels32(px); t.Apply(true, false); t.wrapMode = baseTex.wrapMode; t.filterMode = FilterMode.Trilinear; t.anisoLevel = 4;
            temps.Add(t);
            usedNames = gloss.name + (spec != null ? "+" + spec.name : "");
            return t;
        }
        static Texture2D Sibling(string basePath, string kind)
        {
            foreach (var from in new[] { "Diffuse", "diffuse", "Albedo", "albedo", "BaseColor", "basecolor" })
            {
                // 파일 이름에서만 바꾼다 (폴더 이름에 같은 낱말이 있어도 건드리지 않게)
                string folder = Path.GetDirectoryName(basePath).Replace('\\', '/'), file = Path.GetFileName(basePath);
                if (file.IndexOf(from, StringComparison.Ordinal) < 0) continue;
                string to = char.IsUpper(from[0]) ? kind : kind.ToLowerInvariant();
                var tx = AssetDatabase.LoadAssetAtPath<Texture2D>(folder + "/" + file.Replace(from, to));
                if (tx != null) return tx;
            }
            return null;
        }
        // 텍스처의 파일 값 그대로(sRGB 로 임포트된 것을 sRGB 타깃에 다시 써서 원래 바이트를 얻는다)
        static Color32[] ReadBytes(Texture src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0); t.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = t.GetPixels32(); UnityEngine.Object.DestroyImmediate(t);
            return px;
        }

        /// <summary>일반 텍스처로 임포트된 노멀맵의 NormalMap 타입 사본 (원본 에셋은 그대로 둔다). 한 번 만들면 다시 쓴다.</summary>
        static Texture2D NormalCopy(Texture tex)
        {
            string src = AssetDatabase.GetAssetPath(tex); if (string.IsNullOrEmpty(src)) return null;
            string dst = FixDir + "/" + Path.GetFileNameWithoutExtension(src) + "_asNormalMap" + Path.GetExtension(src);
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(dst) == null)
            {
                FBX2NPRConverter.EnsureFolder(FixDir);
                if (!AssetDatabase.CopyAsset(src, dst)) return null;
                var ti = AssetImporter.GetAtPath(dst) as TextureImporter; if (ti == null) return null;
                ti.textureType = TextureImporterType.NormalMap; ti.convertToNormalmap = false;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }

        /// <summary>팔을 아래로 deg 만큼 내리고 고개를 돌린다. 이름이 LeftArm/RightArm 으로 끝나는 본 (믹사모·유니티짱 공통). 움직인 본 수를 돌려준다.</summary>
        public static int Pose(Transform root, FBX2NPRAssetSettings crit, float armDeg, float headDeg)
        {
            int n = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                string nm = t.name.ToLowerInvariant();
                if (!(nm.EndsWith("leftarm") || nm.EndsWith("rightarm")) || t.childCount == 0) continue;
                Vector3 dir = (t.GetChild(0).position - t.position).normalized; if (dir.sqrMagnitude < 1e-8f) continue;
                Vector3 target = Vector3.RotateTowards(dir, Vector3.down, armDeg * Mathf.Deg2Rad, 0f);
                t.rotation = Quaternion.FromToRotation(dir, target) * t.rotation; n++;
            }
            var h = FBX2NPRConverter.FindHeadBone(root, crit.headBoneContains);
            if (h != null) { h.Rotate(Vector3.up, headDeg, Space.World); n++; }
            return n;
        }

        /// <summary>프리팹과 그 안의 리그·렌더러·메시·재질의 식별자를 한 줄로 (다시 변환한 뒤와 견준다).</summary>
        static string Ids(string prefabPath)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath); if (go == null) return "없음";
            var sb = new StringBuilder(); string g; long id;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(go, out g, out id); sb.Append("prefab " + g + ":" + id);
            var rig = go.GetComponentInChildren<ToonFaceRig>(true);
            if (rig != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(rig, out g, out id)) sb.Append(" | rig " + id);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(r, out g, out id); sb.Append(" | " + r.name + " " + id);
                var smr = r as SkinnedMeshRenderer; var mf = r.GetComponent<MeshFilter>(); var mesh = smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);
                if (mesh != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out g, out id)) sb.Append(" mesh " + g);
                foreach (var m in r.sharedMaterials) if (m != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(m, out g, out id)) sb.Append(" mat " + g);
            }
            return sb.ToString();
        }

        /// <summary>바운드가 화면에 들어오게 viewDir 쪽에서 찍는다 (Vector3.forward 면 정면).</summary>
        public static void Shoot(Bounds bb, int w, int h, string file, Vector3 viewDir, bool shadows, float margin)
        {
            var camGo = new GameObject("Cam"); camGo.hideFlags = HideFlags.HideAndDontSave;
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.20f, 0.20f, 0.22f);
            cam.fieldOfView = FovY; cam.aspect = (float)w / h;
            cam.allowHDR = false; cam.allowMSAA = false;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None; acd.dithering = false; acd.renderShadows = shadows;
            float halfH = Mathf.Max(bb.size.y * 0.5f, bb.size.x * 0.5f / cam.aspect);
            float dist = halfH / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * margin + bb.size.z * 0.5f;
            camGo.transform.position = bb.center + viewDir.normalized * dist;
            camGo.transform.rotation = Quaternion.LookRotation(bb.center - camGo.transform.position, Vector3.up);
            float r = bb.extents.magnitude;
            cam.nearClipPlane = Mathf.Max(0.01f, dist - r * 3f); cam.farClipPlane = dist + r * 6f;

            int ss = Mathf.Max(1, SS), W2 = w * ss, H2 = h * ss;
            var rt = new RenderTexture(W2, H2, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); rt.Create();
            bool done = false;
            try { var rq = new UniversalRenderPipeline.SingleCameraRequest(); rq.destination = rt;
                  if (RenderPipeline.SupportsRenderRequest(cam, rq)) { cam.SubmitRenderRequest(rq); done = true; } } catch { }
            if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(W2, H2, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, W2, H2), 0, 0); t.Apply(); RenderTexture.active = prev;
            if (ss > 1)
            {
                // 빛의 양(리니어)으로 평균해 줄인다. sRGB 값 그대로 평균하면 선 가장자리가 어둡게 뜬다.
                var src = t.GetPixels32(); var dst = new Color32[w * h];
                if (s_toLin == null) { s_toLin = new float[256]; for (int i = 0; i < 256; i++) s_toLin[i] = Mathf.GammaToLinearSpace(i / 255f); }
                float inv = 1f / (ss * ss);
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    float ar = 0f, ag = 0f, ab = 0f;
                    for (int dy = 0; dy < ss; dy++) for (int dx = 0; dx < ss; dx++) { var p = src[(y * ss + dy) * W2 + x * ss + dx]; ar += s_toLin[p.r]; ag += s_toLin[p.g]; ab += s_toLin[p.b]; }
                    dst[y * w + x] = new Color32((byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(ar * inv) * 255f), (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(ag * inv) * 255f), (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(ab * inv) * 255f), 255);
                }
                UnityEngine.Object.DestroyImmediate(t);
                t = new Texture2D(w, h, TextureFormat.RGBA32, false, false); t.SetPixels32(dst); t.Apply();
            }
            File.WriteAllBytes(file, t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(camGo);
        }

        // 몸 평탄화 범위 세 가지(0 얼굴만 / 1 피부까지 / 2 옷까지)를 같은 구도로 찍는다. 팔을 내린 자세, 빛은 화면 왼쪽.
        // 결과: NormalTestOut/convert/ 의 <캐릭터>_bodymode<N>_{upper,full,face}.png 와 log_bodyvariants.txt. 끝나면 모델 설정대로 다시 변환해 둔다.
        public static void RunBodyVariants()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Log.Clear(); SS = 1;
            Directory.CreateDirectory(OutDir);
            var feat = FBX2NPRConverter.FindFeature();
            bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold, feat.settings.debug } : null;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var only = Environment.GetEnvironmentVariable("FBX2NPR_ONLY");
                var models = new List<string>();
                foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (!p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(';'), Path.GetFileNameWithoutExtension(p)) < 0) continue;
                    models.Add(p);
                }
                models.Sort();
                foreach (var m in models)
                {
                    string c = Path.GetFileNameWithoutExtension(m);
                    var imp = AssetImporter.GetAtPath(m);
                    var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
                    for (int mode = 0; mode <= 2; mode++)
                    {
                        FBX2NPRConverter.BodyFlattenOverride = mode;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var r = FBX2NPRConverter.Convert(m);
                        AssetDatabase.SaveAssets();
                        L(""); L(string.Format(CultureInfo.InvariantCulture, "==================== {0} · 몸 평탄화 {1} ({2:F1}초)", c, mode, sw.Elapsed.TotalSeconds));
                        foreach (var line in r.lines) if (line.Contains("평탄화") || line.Contains("텍스처")) L("  " + line);
                        if (r.prefabPath == null) { L("  변환 실패"); continue; }
                        // 평탄화 텍스처의 해시를 남긴다 (범위 0 은 몸 평탄화를 넣기 전과 같아야 한다)
                        string texDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, Path.GetDirectoryName(r.prefabPath), "Textures");
                        if (Directory.Exists(texDir))
                            foreach (var fp in Directory.GetFiles(texDir, "*_flat.png"))
                                using (var md5 = System.Security.Cryptography.MD5.Create())
                                    L("  " + Path.GetFileName(fp) + " md5 " + BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(fp))).Replace("-", ""));
                        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                        var sunGo = new GameObject("Sun"); var sun = sunGo.AddComponent<Light>();
                        sun.type = LightType.Directional; sun.intensity = 1f; sun.shadows = LightShadows.None; RenderSettings.sun = sun;
                        sunGo.transform.rotation = Quaternion.LookRotation(-FBX2NPRConverter.LightDir(35f, 25f), Vector3.up);
                        var toon = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(r.prefabPath));
                        var rig = toon.GetComponentInChildren<ToonFaceRig>(true);
                        Pose(toon.transform, crit, 60f, 20f);
                        toon.SetActive(false); toon.SetActive(true);   // 스킨 메시가 본 변화를 반영하게
                        SS = 2;
                        MipBias(toon, Mathf.Log(SS, 2f));
                        if (rig != null) { rig.ApplyOutline(); if (feat != null) feat.settings.thickness = rig.outlineThickness * SS; rig.Apply(); }
                        if (feat != null) { feat.SetActive(true); Dirty(); }
                        var b = r.body;
                        var upper = new Bounds(new Vector3(b.center.x, b.max.y - b.size.y * 0.275f, b.center.z), new Vector3(b.size.y * 0.55f, b.size.y * 0.55f, b.size.z));
                        Shoot(upper, 900, 900, Path.Combine(OutDir, c + "_bodymode" + mode + "_upper.png"), Vector3.forward, false, 1.08f);
                        Shoot(b, 700, 900, Path.Combine(OutDir, c + "_bodymode" + mode + "_full.png"), Vector3.forward, false, 1.12f);
                        Shoot(r.head, 700, 700, Path.Combine(OutDir, c + "_bodymode" + mode + "_face.png"), Vector3.forward, false, 1.18f);
                        if (feat != null) { feat.SetActive(false); Dirty(); }
                        RestoreMipBias();
                        UnityEngine.Object.DestroyImmediate(toon); UnityEngine.Object.DestroyImmediate(sunGo);
                    }
                    FBX2NPRConverter.BodyFlattenOverride = -1;
                    var back = FBX2NPRConverter.Convert(m); AssetDatabase.SaveAssets();
                    L("  → 모델 설정(bodyFlatten = " + crit.bodyFlatten + ")대로 다시 변환해 둠: " + (back.prefabPath ?? "실패"));
                }
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                FBX2NPRConverter.BodyFlattenOverride = -1; RestoreMipBias();
                if (feat != null && keep != null)
                {
                    feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2];
                    feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.settings.debug = keep[5];
                    feat.SetActive(featWas); Dirty();
                }
            }
            File.WriteAllText(Path.Combine(OutDir, "log_bodyvariants.txt"), Log.ToString(), new UTF8Encoding(true));
        }
        static float[] s_toLin;

        // 2배로 찍을 때는 텍스처 밉을 한 단계 올려, 1배로 찍을 때와 같은 밉을 읽게 한다. 그대로 두면 더 세밀한 밉을 읽어
        // 알파 컷아웃 카드(속눈썹·머리카락)가 가닥마다 점으로 쪼개져 1배 화면과 다른 그림이 된다. 원래 값은 끝나면 되돌린다.
        static readonly Dictionary<Texture, float> s_bias = new Dictionary<Texture, float>();
        static void MipBias(GameObject root, float bias)
        {
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    foreach (int id in m.GetTexturePropertyNameIDs())
                    {
                        var tx = m.GetTexture(id); if (tx == null) continue;
                        if (!s_bias.ContainsKey(tx)) s_bias[tx] = tx.mipMapBias;
                        tx.mipMapBias = s_bias[tx] + bias;
                    }
                }
        }
        static void RestoreMipBias()
        {
            foreach (var kv in s_bias) if (kv.Key != null) kv.Key.mipMapBias = kv.Value;
            s_bias.Clear();
        }

        static void Dirty()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath); if (rd == null) return;
            var m = rd.GetType().GetMethod("SetDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (m != null) m.Invoke(rd, null);
        }
    }
}
