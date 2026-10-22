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
    /// 얼굴 명암 미리보기 씬 생성기 (계획서 목표 11의 첫 형태: 3D 도구 없이 유니티 화면에서 조절).
    ///
    /// 테스트 캐릭터마다 씬을 하나 만든다: 노멀 정리된 메시 + 툰 재질 + ToonFaceRig(슬라이더) + 표식(빛·코끝·눈 A·눈 B).
    /// 씬을 열고 '얼굴 명암 리그'를 선택하면 명암선 위치·부드러움·코 음영·눈 피하기·림을 슬라이더로 바꾸고,
    /// 표식을 이동·회전해 빛 방향과 코·눈 영역을 바꿀 수 있다. 코끝·눈 표식의 처음 자리는 FaceLandmarks 가 자동으로 찾는다.
    ///
    /// 메시 처리 규칙(머리 본 가중치, 모델별 얼굴 판정, 머리 아래 정적 메시, 얼굴 전체에서 한 번 구한 중심으로 방사형 노멀)은
    /// FaceMeshRule 한곳에 있다 — 변환기·임포터·실험과 공용. 데칼 무시, 원본 노멀은 UV3.
    /// </summary>
    public static class ToonFacePreview
    {
        const string OutRoot = "Assets/FBX2NPRPreview";
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const float FovY = 24f;
        const int Res = 700;
        // 손으로 칠한 알베도는 얼굴도 실루엣 외곽선만 쓴다 (규칙 9). 알베도 지표로 자동 판정하는 것은 남은 일.
        static readonly HashSet<string> HandPainted = new HashSet<string> { "unitychan" };
        // 59번 코 초승달 기본값: 길이 1.6배, 가장 두꺼운 곳은 아래쪽(-0.35) — 비교 시트 gen6/nose_long_crescent.png 의 A
        // 60번: "너무 얇다" → 두께·바깥 폭 비교 (gen6/nose_crescent_thickness.png). 두께만 1 가까이 올리면 반달이 된다.
        // 62번: 사용자가 물방울(61번)보다 두께 0.75 + 폭 1.5배 초승달을 골랐다.
        const float MoonStretchDefault = 1.6f, MoonBulgeDefault = -0.35f, MoonThicknessDefault = 0.75f, MoonWidthDefault = 1.5f;
        // 61번 물방울 기본값 (사용자 레퍼런스 일러스트의 비율, gen6/nose_teardrop.png 의 ②)
        const float DropRadiusDefault = 0.13f, DropHeightDefault = 0.37f;

        class Part
        {
            public string name;
            public Mesh mesh;
            public Vector3[] origN;
            public Matrix4x4 l2w;
            public float[] weights;     // 얼굴 가중치 (얼굴이 아니면 0)
            public float[] boundsW;     // 프레이밍용 머리 본 가중치
            public int headCount;
            public bool isFace;
            public Texture albedo;
        }

        [MenuItem("FBX2NPR/얼굴 명암 미리보기 씬 만들기 (테스트 캐릭터 전부)")]
        public static void BuildAllMenu()
        {
            var paths = BuildAll();
            if (paths.Count > 0) EditorSceneManager.OpenScene(paths[0]);
            EditorUtility.DisplayDialog("FBX2NPR", "미리보기 씬 " + paths.Count + "개를 만들었습니다.\n" + OutRoot + " 아래에 있습니다.\n씬을 열고 '얼굴 명암 리그'를 선택해 조절하세요.", "확인");
        }

        public static List<string> BuildAll()
        {
            var list = new List<string>();
            foreach (var c in Chars()) { var p = Build(c); if (p != null) list.Add(p); }
            AssetDatabase.SaveAssets();
            return list;
        }

        static string[] Chars()
        {
            var list = new List<string>();
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) list.Add(Path.GetFileNameWithoutExtension(p));
            }
            list.Sort();
            return list.ToArray();
        }

        static string PathOf(string c)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(p) == c) return p;
            }
            return null;
        }

        static Vector3 DefaultLight()
        {
            float e = 25f * Mathf.Deg2Rad, a = 35f * Mathf.Deg2Rad;
            return (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a)) + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a)) + Vector3.up * Mathf.Sin(e)).normalized;
        }

        /// <summary>캐릭터 하나의 미리보기 씬을 만들고 경로를 돌려준다.</summary>
        public static string Build(string c)
        {
            string path = PathOf(c);
            var prefab = path != null ? AssetDatabase.LoadAssetAtPath<GameObject>(path) : null;
            if (prefab == null) { Debug.LogWarning("[FBX2NPR 미리보기] 모델 없음: " + c); return null; }
            var imp = AssetImporter.GetAtPath(path);
            var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();

            string dir = OutRoot + "/" + c;
            EnsureFolder(dir + "/Meshes"); EnsureFolder(dir + "/Materials");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var parts = CollectParts(inst, crit, path);
            UnityEngine.Object.DestroyImmediate(inst);

            // 원본 노멀은 UV3 (외곽선 검출과 코 음영이 쓴다), 조명용 노멀은 방사형 (얼굴 전체에서 한 번 구한 중심).
            foreach (var p in parts) p.mesh.SetUVs(3, new List<Vector3>(p.origN));
            var targets = new List<FaceMeshRule.Target>();
            foreach (var p in parts) targets.Add(new FaceMeshRule.Target { vertices = p.mesh.vertices, origN = p.origN, l2w = p.l2w, weights = p.weights, count = p.headCount });
            Vector3 centerW = FaceMeshRule.Centroid(targets);
            for (int k = 0; k < parts.Count; k++)
            {
                if (parts[k].headCount == 0) continue;
                parts[k].mesh.normals = FaceMeshRule.Apply(targets[k], centerW, 1f);
            }

            // 기준점과 프레이밍
            var srcs = new List<FaceLandmarks.Src>();
            var framing = new Bounds(); bool fInit = false;
            foreach (var p in parts)
            {
                var v = p.mesh.vertices; var wv = new Vector3[v.Length]; var wn = new Vector3[v.Length];
                var nm = p.l2w.inverse.transpose;   // 원본 노멀을 월드로 (코 음영 임계값 보정용)
                for (int i = 0; i < v.Length; i++)
                {
                    wv[i] = p.l2w.MultiplyPoint3x4(v[i]);
                    wn[i] = nm.MultiplyVector(p.origN[i]).normalized;
                    if (p.boundsW[i] > 0f) { if (!fInit) { framing = new Bounds(wv[i], Vector3.zero); fInit = true; } else framing.Encapsulate(wv[i]); }
                }
                srcs.Add(new FaceLandmarks.Src { name = p.name, world = wv, weights = p.weights, isFace = p.isFace, normals = wn, triangles = p.mesh.triangles });
            }
            var marks = FaceLandmarks.Detect(srcs);
            if (!marks.faceFound || !fInit) { Debug.LogWarning("[FBX2NPR 미리보기] 얼굴 메시 없음: " + c); return null; }
            marks.CalibrateNoseStop(srcs, DefaultLight());   // 53번: 코가 평평한 캐릭터(유니티짱)는 임계값을 올려 반달을 살린다
            // 54~59번: 그 반달 자리에 깔끔한 도형. 기본은 59번 늘린 초승달(길이 1.6배, 코끝 쪽이 두껍고 콧대 쪽으로 가늘게).
            // 세모·둥근 사다리꼴은 리그에서 고를 수 있다 (둥근 사다리꼴 꼭짓점도 같이 채운다). 길이는 리그가 적용하므로 여기서는 1.
            // 62번: 기본은 초승달(길이 1.6, 두께 0.75, 폭 1.5배, 코끝 쪽이 두꺼움, 사용자 선택). 물방울·세모·둥근 사다리꼴 값도 채워 리그에서 바꿀 수 있다.
            marks.FitNoseMoon(srcs, DefaultLight(), MoonThicknessDefault, 0, false, 1f, MoonBulgeDefault, 1f, DropRadiusDefault, DropHeightDefault);
            Debug.Log("[FBX2NPR 미리보기] " + c + " " + marks.noseStopNote + " / " + marks.moonNote);

            bool silFace = HandPainted.Contains(c);
            var rootGo = new GameObject(c + " 얼굴 명암 리그");
            var rig = rootGo.AddComponent<ToonFaceRig>();
            var shader = Shader.Find("NormalTest/ToonBand");
            var partsGo = new GameObject("메시"); partsGo.transform.SetParent(rootGo.transform, false);
            int idx = 0;
            foreach (var p in parts)
            {
                string safe = Safe(p.name) + "_" + (idx++);
                string meshPath = dir + "/Meshes/" + safe + ".asset";
                AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(p.mesh, meshPath);

                var mat = new Material(shader);
                SetupMat(mat, p, silFace, framing.center);
                string matPath = dir + "/Materials/" + safe + ".mat";
                AssetDatabase.DeleteAsset(matPath);
                AssetDatabase.CreateAsset(mat, matPath);

                var go = new GameObject(p.name); go.transform.SetParent(partsGo.transform, false);
                go.transform.position = p.l2w.GetColumn(3);
                go.transform.rotation = p.l2w.rotation;
                go.transform.localScale = p.l2w.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = p.mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;   // 실험 코드와 같이 파트당 재질 하나
                mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
                if (p.isFace) rig.faceMaterials.Add(mat); else rig.otherMaterials.Add(mat);
            }

            // 표식
            Vector3 L = DefaultLight();
            var lightGo = new GameObject("빛 (회전해서 방향 조절)"); lightGo.transform.SetParent(rootGo.transform, false);
            lightGo.transform.position = framing.center + L * (framing.extents.magnitude * 1.6f);
            lightGo.transform.rotation = Quaternion.LookRotation(-L, Vector3.up);
            rig.lightTransform = lightGo.transform;
            var fcGo = new GameObject("얼굴 중심"); fcGo.transform.SetParent(rootGo.transform, false);
            fcGo.transform.position = framing.center; rig.faceCenter = fcGo.transform;
            if (marks.noseFound)
            {
                var n = new GameObject("코끝 (옮겨서 코 음영 위치 조절)"); n.transform.SetParent(rootGo.transform, false);
                n.transform.position = marks.noseTip; rig.noseTip = n.transform;
            }
            rig.noseRadius = marks.noseRadius;
            if (marks.eyesFound)
            {
                var a = new GameObject("눈 A (옮겨서 눈 피하기 위치 조절)"); a.transform.SetParent(rootGo.transform, false); a.transform.position = marks.eyeA; rig.eyeA = a.transform;
                var b = new GameObject("눈 B (옮겨서 눈 피하기 위치 조절)"); b.transform.SetParent(rootGo.transform, false); b.transform.position = marks.eyeB; rig.eyeB = b.transform;
                rig.eyeRadius = new Vector3(marks.eyeRadius.x, marks.eyeRadius.y, marks.eyeRadius.z);
            }
            // 추정한 눈은 틀린 자리일 수 있어 끈 채로 둔다. 표식을 제자리로 옮긴 뒤 켜면 된다.
            rig.avoidEyes = false;   // 49번: 명암선이 눈 바깥을 지나므로 기본은 끔. 선을 안쪽으로 옮길 때 켜는 선택 기능
            // 52번: 코 임계값은 캐릭터마다 자동 보정 — 기본 0(콧구멍 바로 위 물방울), 코가 평평하면 최소 면적이 될 때까지 올린다(유니티짱 반달).
            // 영역(noseRadius)은 FaceLandmarks 가 눈 기준으로 잡는다.
            rig.linePosition = -0.20f; rig.lineSoftness = 0.015f; rig.noseThreshold = marks.noseStop; rig.noseStrength = 0.6f; rig.rim = 0.30f;
            rig.noseHalfMoon = marks.moonOn;
            if (marks.moonOn)
            {
                rig.moonShape = marks.moonShape; rig.moonParam = marks.moonParam; rig.moonThickness = marks.moonParam.z;
                rig.noseFigure = (ToonFaceRig.NoseFigure)Mathf.RoundToInt(marks.moonParam.w);
                rig.quadA = marks.quadA; rig.quadB = marks.quadB; rig.quadRound = FaceLandmarks.QuadRound;
                rig.moonStretch = MoonStretchDefault; rig.moonBulge = marks.moonExtra.x; rig.moonWidth = MoonWidthDefault;
                rig.dropA = marks.dropA; rig.dropR = marks.dropR; rig.dropScale = 1f; rig.dropLength = 1f;
            }
            rig.chinScale = marks.ChinDrop(); rig.chinTilt = 1.0f; rig.chinBend = 0.35f;

            var feat = FindFeature();
            if (feat != null)
            {
                feat.settings.depthScale = 12f; feat.settings.normalScale = 0.8f; feat.settings.threshold = 0.30f;
                feat.settings.thickness = 1.0f; feat.settings.darkness = 0.35f; feat.settings.debug = 0f;
                rig.outlineFeature = feat; rig.outline = true;
            }
            rig.Apply();

            // 카메라: 실험 렌더와 같은 정면 구도
            var camGo = new GameObject("카메라"); camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black; cam.fieldOfView = FovY;
            cam.allowHDR = false; cam.allowMSAA = false;
            var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
            acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None; acd.dithering = false; acd.renderShadows = false;
            float r = Mathf.Max(framing.size.x, Mathf.Max(framing.size.y, framing.size.z)) * 0.5f;
            float dist = r / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * 1.25f;
            camGo.transform.position = framing.center + Vector3.forward * dist;
            camGo.transform.rotation = Quaternion.LookRotation(framing.center - camGo.transform.position, Vector3.up);
            cam.nearClipPlane = Mathf.Max(0.001f, dist - r * 3f); cam.farClipPlane = dist + r * 8f;

            Selection.activeGameObject = rootGo;
            string scenePath = dir + "/Preview_" + c + ".unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            Debug.Log("[FBX2NPR 미리보기] " + scenePath + " / " + marks.Describe());
            return scenePath;
        }

        static void SetupMat(Material mat, Part p, bool silFace, Vector3 faceCenter)
        {
            mat.SetVector("_Band0", new Vector4(0.32f, 0.30f, 0.42f, 1f));
            // 그림자 색은 원래 회보라 (58번 살구빛은 사용자가 되돌림, 59번). 얼굴은 리그 skinShadow 로 바꿀 수 있다.
            mat.SetVector("_Band1", new Vector4(0.72f, 0.66f, 0.70f, 1f));
            mat.SetVector("_Band2", new Vector4(1.00f, 0.96f, 0.92f, 1f));
            mat.SetFloat("_Stop1", 0.0f); mat.SetFloat("_Stop2", 0.2f);   // 얼굴이 아닌 메시는 2톤 밴드
            mat.SetFloat("_UseOrigNormal", 1f); mat.SetFloat("_NormalStrength", 0f);
            mat.SetFloat("_OutlineFlatNormal", (silFace || !p.isFace) ? 1f : 0f);
            if (p.albedo != null) mat.SetTexture("_Albedo", p.albedo);
            mat.SetFloat("_AlbedoOn", p.albedo != null ? 1f : 0f);
            mat.SetFloat("_Cutout", (!p.isFace && p.albedo != null) ? 1f : 0f); mat.SetFloat("_Cutoff", 0.5f);
            mat.SetFloat("_Posterize", 0f); mat.SetFloat("_PosterizeLum", 0f);
            mat.SetFloat("_Flatten", 0f); mat.SetFloat("_Saturation", 1f); mat.SetFloat("_Lift", 0f);
            mat.SetVector("_LightDir", DefaultLight());
            if (p.isFace)
            {
                // 림(빛 쪽 윤곽선 안쪽 밝게)에 필요한 값. 명암선·코·눈 값은 리그가 넣는다.
                mat.SetVector("_FaceCenter", faceCenter);
                mat.SetFloat("_FaceFlat", 0f); mat.SetFloat("_EdgeDark", 0f); mat.SetFloat("_OrigBlend", 0f);
                mat.SetFloat("_EdgeWidth", 0.95f); mat.SetFloat("_EdgeTaper", 0.15f); mat.SetFloat("_EdgeSoft", 0.04f);
                mat.SetFloat("_RimWidth", 0.92f);
            }
        }

        static List<Part> CollectParts(GameObject inst, FBX2NPRAssetSettings crit, string path)
        {
            var parts = new List<Part>();
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var src = smr.sharedMesh; if (src == null) continue;
                if (crit.IsIgnoredMesh(src.name)) continue;
                var p = new Part();
                p.name = src.name;
                p.mesh = UnityEngine.Object.Instantiate(src); p.mesh.name = src.name;
                p.origN = p.mesh.normals;
                p.l2w = smr.transform.localToWorldMatrix;
                p.albedo = ResolveAlbedo(smr.sharedMaterial, path, crit);
                var fw = FaceMeshRule.Weigh(src, smr.bones, smr.sharedMaterials, crit);
                p.weights = fw.face; p.boundsW = fw.head; p.headCount = fw.faceCount; p.isFace = fw.isFace;
                parts.Add(p);
            }
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.sharedMesh; if (src == null) continue;
                if (crit.IsIgnoredMesh(src.name)) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                var mats = mr != null ? mr.sharedMaterials : null;
                var p = new Part();
                p.name = src.name;
                p.mesh = UnityEngine.Object.Instantiate(src); p.mesh.name = src.name;
                p.origN = p.mesh.normals;
                p.l2w = mf.transform.localToWorldMatrix;
                p.albedo = ResolveAlbedo(mats != null && mats.Length > 0 ? mats[0] : null, path, crit);
                var fw = FaceMeshRule.WeighStatic(src, mf.transform, inst.transform, mats, crit);
                p.weights = fw.face; p.boundsW = fw.head; p.headCount = fw.faceCount; p.isFace = fw.isFace;
                parts.Add(p);
            }
            return parts;
        }

        static Texture ResolveAlbedo(Material m, string modelPath, FBX2NPRAssetSettings s)
        {
            if (m == null) return null;
            if (m.mainTexture != null) return m.mainTexture;
            string want = s.AlbedoNameFor(m.name); if (want == null) return null;
            string root = Path.GetDirectoryName(Path.GetDirectoryName(modelPath)).Replace(Path.DirectorySeparatorChar, '/');
            foreach (var g in AssetDatabase.FindAssets(want + " t:Texture2D", new[] { root }))
            {
                var ap = AssetDatabase.GUIDToAssetPath(g);
                if (Path.GetFileNameWithoutExtension(ap) == want) return AssetDatabase.LoadAssetAtPath<Texture2D>(ap);
            }
            return null;
        }

        static SobelOutlineFeature FindFeature()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd == null) return null;
            foreach (var f in rd.rendererFeatures) { var s = f as SobelOutlineFeature; if (s != null) return s; }
            return null;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static string Safe(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s.Replace(':', '_');
        }

        // ---------- 배치 확인: 씬을 만들고, 리그 값을 바꿔 가며 카메라로 찍어 조절이 실제로 먹는지 본다 ----------

        public static void BuildAndVerify()
        {
            var log = new StringBuilder();
            string outDir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "preview");
            Directory.CreateDirectory(outDir);
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var paths = BuildAll();
                log.AppendLine("씬 " + paths.Count + "개");
                foreach (var sp in paths)
                {
                    EditorSceneManager.OpenScene(sp);
                    var rig = UnityEngine.Object.FindFirstObjectByType<ToonFaceRig>();
                    var cam = UnityEngine.Object.FindFirstObjectByType<Camera>();
                    string c = Path.GetFileNameWithoutExtension(sp).Replace("Preview_", "");
                    log.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1} / 얼굴 재질 {2}, 기타 재질 {3}, 코끝 {4}, 눈 {5}, 눈 피하기 기본 {6}",
                        c, sp, rig.faceMaterials.Count, rig.otherMaterials.Count, rig.noseTip != null, rig.eyeA != null, rig.avoidEyes));

                    Shoot(cam, Path.Combine(outDir, c + "_1_default.png"));
                    rig.linePosition = 0.10f; rig.Apply(); Shoot(cam, Path.Combine(outDir, c + "_2_line010.png"));
                    bool eyeWas = rig.avoidEyes;
                    rig.avoidEyes = !eyeWas; rig.Apply(); Shoot(cam, Path.Combine(outDir, c + "_3_line010_eye" + (eyeWas ? "Off" : "On") + ".png"));
                    rig.avoidEyes = eyeWas;
                    // 빛을 반대쪽(방위 -35도)으로 돌리면 명암선도 반대쪽으로 가야 한다
                    var lt = rig.lightTransform; var rotWas = lt.rotation;
                    float e = 25f * Mathf.Deg2Rad, a = -35f * Mathf.Deg2Rad;
                    var L2 = (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a)) + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a)) + Vector3.up * Mathf.Sin(e)).normalized;
                    lt.rotation = Quaternion.LookRotation(-L2, Vector3.up); rig.Apply(); Shoot(cam, Path.Combine(outDir, c + "_4_line010_lightOther.png"));
                    // 원래 값으로 돌려놓는다 (재질 에셋이 기본값으로 남도록)
                    lt.rotation = rotWas; rig.linePosition = -0.20f; rig.Apply();
                }
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex) { log.AppendLine("예외: " + ex); }
            File.WriteAllText(Path.Combine(outDir, "log_preview.txt"), log.ToString(), new UTF8Encoding(true));
        }

        static void Shoot(Camera cam, string file)
        {
            var rt = new RenderTexture(Res, Res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); rt.Create();
            bool done = false;
            try
            {
                var rq = new UniversalRenderPipeline.SingleCameraRequest(); rq.destination = rt;
                if (RenderPipeline.SupportsRenderRequest(cam, rq)) { cam.SubmitRenderRequest(rq); done = true; }
            }
            catch { }
            if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(Res, Res, TextureFormat.RGBA32, false, false);
            t.ReadPixels(new Rect(0, 0, Res, Res), 0, 0); t.Apply(); RenderTexture.active = prev;
            File.WriteAllBytes(file, t.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(t); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
