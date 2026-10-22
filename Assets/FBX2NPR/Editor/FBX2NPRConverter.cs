using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 모델 하나를 '바로 쓸 수 있는 툰 캐릭터 프리팹'으로 바꾸는 한 경로.
    ///
    /// 지금까지 따로 있던 세 조각을 순서대로 한 번에 한다.
    ///   1) 얼굴 노멀 정리 (임포터·미리보기와 같은 규칙: 머리 본 가중치, 얼굴 전체에서 한 번 구한 중심)
    ///   2) 얼굴 텍스처에서 구운 빛을 빼고 피부를 평평하게 (SkinFlatBake). 이미 평평한 텍스처와 피부가 한 색이 아닌 텍스처는 건너뛴다.
    ///   3) 얼굴 재질(명암선·코 음영·림)과 옷·머리카락 재질(일반 툰 밴드, 실루엣 선) 생성과 할당
    /// 결과는 뼈대와 스킨이 그대로인 프리팹이라 애니메이션이 된다. 원본 FBX 와 텍스처는 건드리지 않는다.
    ///
    /// 스킨 메시에서 원본 노멀은 탄젠트 자리에 싣는다. UV3 에 실으면 뼈를 따라 돌지 않아 고개를 돌릴 때
    /// 코 음영과 외곽선이 어긋난다 (ToonBand 의 _OrigFromTangent).
    /// 얼굴 기준점(코끝·눈·얼굴 중심)은 머리 본 아래에 표식으로 두어 머리와 같이 움직이게 한다.
    /// 빛은 씬의 디렉셔널 라이트를 따른다 (ToonFaceRig.LightDirection).
    ///
    /// 다시 변환하면 같은 경로의 프리팹·메시·재질을 지우지 않고 내용만 바꾼다. 씬에 놓아 둔 프리팹의 연결이 끊기지 않게 하기 위해서다.
    /// 다만 리그의 슬라이더 값은 변환 도구의 기본값으로 돌아간다 (씬 인스턴스에서 바꾼 값은 인스턴스 오버라이드로 남는다).
    ///
    /// 전제: 모델이 +Z 를 보고 +Y 가 위다. 블렌드셰이프(표정)가 있는 메시는 시험하지 않았다 (경고만 낸다).
    /// 메시 처리 규칙은 GeneralizationTest·ToonFacePreview 와 같다. 세 곳에 같은 규칙이 있으므로 고칠 때 같이 볼 것.
    /// </summary>
    public static class FBX2NPRConverter
    {
        public const string OutRoot = "Assets/FBX2NPRConverted";
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const string OutlineShaderPath = "Assets/FBX2NPR/SobelOutline.shader";
        const float WeightThreshold = 0.5f;
        // 미리보기 생성기와 같은 코 도형 기본값 (62번: 두꺼운 초승달)
        const float MoonStretchDefault = 1.6f, MoonBulgeDefault = -0.35f, MoonThicknessDefault = 0.75f, MoonWidthDefault = 1.5f;
        const float DropRadiusDefault = 0.13f, DropHeightDefault = 0.37f;
        /// <summary>
        /// 평탄화 색 검사: 칠할 평탄색과 평탄화 대상 영역의 원래 평균색 사이의 색도(밝기를 뺀 색) 거리가 이보다 크면
        /// '피부가 한 색이 아니다'로 보고 평탄화를 건너뛴다. 머리 전체의 색이 바뀌는 실패(Jones: 녹색 머리가 베이지로)를 막는다.
        /// 평탄화가 도는 세 텍스처로만 맞춘 값이다: Ch07 0.014, The Boss 0.007 은 통과, Jones 0.040 은 걸림 (그 사이의 값).
        /// </summary>
        public const float FlatColorGapMax = 0.025f;
        // 파서가 눈·눈썹을 '찾았다'고 볼 최소 비율(관측 텍셀 대비). Ch07 눈 1.7% 눈썹 0.6%, The Boss 눈 0.0% 눈썹 0 (78번) — 두 체로 정한 값
        public const float ParserEyeMinFrac = 0.002f, ParserBrowMinFrac = 0.001f;
        /// <summary>도구가 만든 에셋(메시·재질·평탄화 텍스처)에 붙이는 라벨. 찾아보기용 표시이고, 정리는 리그에 적어 둔 목록으로 한다.</summary>
        public const string GeneratedLabel = "FBX2NPRGenerated";
        /// <summary>실험·비교용: 0 이상이면 모델 설정의 bodyFlatten 대신 이 값을 쓴다 (0 얼굴만, 1 피부까지, 2 옷까지).</summary>
        public static int BodyFlattenOverride = -1;
        /// <summary>시험 스크립트용: 모델 설정의 style 대신 쓸 값 (-1 이면 설정대로). 0 툰, 1 회화풍.</summary>
        public static int StyleOverride = -1;
        /// <summary>
        /// 얼굴 안쪽 선은 얼굴이 화면에서 작아지면 한 픽셀에 코·입의 굴곡이 통째로 들어가 갈색 덩어리로 뭉친다 (전신 구도에서 실측).
        /// 얼굴 높이가 화면에서 이 픽셀 수보다 작아지면 지우기 시작해(Start) 다 지운다(End). 그 뒤로는 실루엣 선만 남는다.
        /// </summary>
        public const float InnerLineFadeStartPx = 380f, InnerLineFadeEndPx = 280f;
        /// <summary>설정하면 평탄화 마스크 PNG 를 이 폴더에 쓴다 (디버그용. 에셋으로는 넣지 않는다).</summary>
        public static string DebugDir;

        public class Report
        {
            public string model, prefabPath;
            public Bounds head, body;                 // 변환할 때 자세의 월드 바운드 (비교 렌더의 구도용)
            public bool faceFound, handPainted, flattenBlocked;
            public List<string> lines = new List<string>();
            public override string ToString() { return string.Join("\n", lines.ToArray()); }
        }

        class Part
        {
            public string name;
            public Renderer rend; public SkinnedMeshRenderer smr; public MeshFilter mf;
            public Mesh mesh; public Vector3[] origN; public Matrix4x4 l2w;
            public float[] weights, boundsW; public int headCount; public bool isFace;
            // 멀어지면 안쪽 선을 지울 범위 (UV2.y): 얼굴 메시(피부 메시)의 얼굴 재질 칸 정점 전부. 그 밖은 0
            public float[] lineFadeW;
            public Material[] srcMats; public Texture albedo;
            // 재질 칸(서브메시)마다: 얼굴 규칙에 맞는 칸인가, 그 칸의 알베도. 얼굴 메시에 속눈썹 카드 같은 다른 재질 칸이 섞인 모델용.
            public bool[] faceSlot; public Texture[] slotAlbedo;
            public float bindDiff; public bool skinRule;   // skinRule: 재질·메시 이름이 피부 규칙에 맞는가 (몸 평탄화에서 피부 후보)
            // 얼굴 재질을 받을 렌더러인가. 재질명 규칙(isFace)만으로는 부족하다 — 믹사모 Ch07 은 옷도 피부와 같은 재질(Ch07_body)을 써서
            // 옷까지 얼굴로 잡힌다. 실제로 얼굴 정점(머리 본 가중치)이 있는 메시만 얼굴 재질을 받는다.
            public bool FaceRend { get { return isFace && headCount > 0; } }
        }

        [MenuItem("FBX2NPR/툰 캐릭터로 변환 (선택한 모델)")]
        static void ConvertSelectedMenu()
        {
            int n = 0; var sb = new StringBuilder();
            foreach (var o in Selection.objects)
            {
                string p = AssetDatabase.GetAssetPath(o);
                if (string.IsNullOrEmpty(p) || !(AssetImporter.GetAtPath(p) is ModelImporter)) continue;
                try
                {
                    var r = Convert(p); if (r != null && r.prefabPath != null) { n++; sb.AppendLine(r.prefabPath); }
                    if (r != null) Debug.Log("[FBX2NPR] " + p + "\n" + r);
                }
                catch (Exception ex) { Debug.LogError("[FBX2NPR] 변환 실패: " + p + "\n" + ex); }
            }
            AssetDatabase.SaveAssets();
            EditorUtility.DisplayDialog("FBX2NPR", n > 0 ? "툰 캐릭터 프리팹 " + n + "개를 만들었습니다.\n" + sb + "\n자세한 기록은 콘솔에 있습니다." : "프로젝트 창에서 모델(FBX)을 선택한 뒤 실행하세요.", "확인");
        }
        [MenuItem("FBX2NPR/툰 캐릭터로 변환 (선택한 모델)", true)]
        static bool ConvertSelectedValidate()
        {
            foreach (var o in Selection.objects) { string p = AssetDatabase.GetAssetPath(o); if (!string.IsNullOrEmpty(p) && AssetImporter.GetAtPath(p) is ModelImporter) return true; }
            return false;
        }

        /// <summary>모델(FBX) 하나를 툰 캐릭터 프리팹으로 바꾼다. 실패하면 prefabPath 가 null.</summary>
        public static Report Convert(string modelPath)
        {
            var rep = new Report { model = modelPath };
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (prefab == null) { rep.lines.Add("모델 없음: " + modelPath); return rep; }
            var imp = AssetImporter.GetAtPath(modelPath);
            var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
            // 2-Track: 회화풍 모드는 별도 경로 (붓질 알베도 + 표준 Lit + 얇은 잉크). 파싱·노멀 정리·평탄화는 하지 않는다.
            if ((StyleOverride >= 0 ? StyleOverride : crit.style) == 1) return FBX2NPRPainterly.Convert(modelPath, prefab, crit, rep);
            string c = Path.GetFileNameWithoutExtension(modelPath);
            string dir = OutRoot + "/" + Safe(c);
            if (crit.enabled) rep.lines.Add("주의: 이 모델은 임포터가 이미 노멀을 정리합니다. 원본 노멀을 얻을 수 없어 코 음영과 얼굴 안쪽 선이 약해집니다. 임포터 설정을 끄고 다시 임포트한 뒤 변환하세요.");
            string prefabPath = dir + "/" + Safe(c) + "_Toon.prefab";
            string modelGuid = AssetDatabase.AssetPathToGUID(modelPath);
            var prevGenerated = new List<string>();   // 지난 변환이 만든 에셋의 GUID (리그에 적어 둔 목록). 정리는 이 목록에 있는 것만 한다
            {
                // 이름이 같은 다른 모델(다른 폴더의 같은 파일명)이 만든 프리팹이면 덮어쓰지 않고 폴더를 따로 쓴다.
                // 이 모델이 전에 따로 쓴 폴더가 있으면 그쪽을 먼저 쓴다 (기본 폴더의 프리팹이 지워져도 옮겨 가지 않게).
                string altDir = OutRoot + "/" + Safe(c) + "_" + modelGuid.Substring(0, 8);
                var altGo = AssetDatabase.LoadAssetAtPath<GameObject>(altDir + "/" + Safe(c) + "_Toon.prefab");
                var oldGo = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var oldRig = oldGo != null ? oldGo.GetComponentInChildren<ToonFaceRig>(true) : null;
                if (altGo != null) { dir = altDir; oldGo = altGo; oldRig = altGo.GetComponentInChildren<ToonFaceRig>(true); }
                else if (oldRig != null && !string.IsNullOrEmpty(oldRig.sourceModelGuid) && oldRig.sourceModelGuid != modelGuid)
                {
                    dir = altDir; oldRig = null;
                    rep.lines.Add("같은 이름의 다른 모델이 이미 변환돼 있어 폴더를 따로 씁니다: " + dir);
                }
                prefabPath = dir + "/" + Safe(c) + "_Toon.prefab";
                if (oldRig != null && oldRig.generatedAssets != null) prevGenerated.AddRange(oldRig.generatedAssets);
            }
            EnsureFolder(dir + "/Meshes"); EnsureFolder(dir + "/Materials"); EnsureFolder(dir + "/Textures");
            bool existed = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
            int bodyMode = BodyFlattenOverride >= 0 ? BodyFlattenOverride : crit.bodyFlatten;

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.name = c + "_Toon";
            try
            {
                Transform headBone = FindHeadBone(inst.transform, crit.headBoneContains);
                var parts = CollectParts(inst, crit, modelPath, rep, headBone);
                bool lashDrawn = false;   // 어느 텍스처에서든 눈매 먹선(아이라인)을 그렸는가 → 속눈썹 카드 메시를 숨길지
                if (parts.Count == 0) { rep.lines.Add("렌더러 없음"); return rep; }

                // ---- 1) 노멀: 원본은 UV3 와 탄젠트에, 조명용은 방사형(얼굴 전체에서 한 번 구한 중심)
                bool blendWarned = false;
                foreach (var p in parts)
                {
                    p.mesh.SetUVs(3, new List<Vector3>(p.origN));
                    var tan = new Vector4[p.origN.Length];
                    for (int i = 0; i < tan.Length; i++) { var n = p.origN[i]; tan[i] = new Vector4(n.x, n.y, n.z, 1f); }
                    p.mesh.tangents = tan;
                    // 얼굴 가중치는 UV2.x 에. 얼굴과 몸이 한 메시일 때 셰이더가 얼굴 명암을 얼굴에만 적용한다 (_FaceWeightOn).
                    // UV2.y 는 피부 메시 전체: 게임 거리에서 안쪽 선을 지우는 범위 (얼굴만 지우면 목·손·발목이 선 색 덩어리로 남는다, Ch07 툰. LineFadeWeights)
                    var fw = new List<Vector2>(p.weights.Length);
                    for (int i = 0; i < p.weights.Length; i++) fw.Add(new Vector2(p.weights[i], p.lineFadeW != null ? p.lineFadeW[i] : 0f));
                    p.mesh.SetUVs(2, fw);
                    if (p.mesh.blendShapeCount > 0 && !blendWarned)
                    {
                        blendWarned = true;
                        rep.lines.Add("주의: 블렌드셰이프(표정)가 있는 메시가 있습니다 (" + p.name + " 등). 표정을 움직일 때의 노멀은 시험하지 않았습니다: 탄젠트 자리의 원본 노멀에 탄젠트용 변화량이 더해져 코 음영·얼굴 안쪽 선이 틀어질 수 있습니다.");
                    }
                }
                var world = new List<Vector3>();
                foreach (var p in parts)
                {
                    if (p.headCount == 0) continue;
                    var v = p.mesh.vertices;
                    for (int i = 0; i < v.Length; i++) if (p.weights[i] > 0f) world.Add(p.l2w.MultiplyPoint3x4(v[i]));
                }
                Vector3 centerW = world.Count > 0 ? RadialNormals.Centroid(world.ToArray()) : Vector3.zero;
                int cleaned = 0;
                foreach (var p in parts)
                {
                    if (p.headCount == 0) continue;
                    p.mesh.normals = RadialNormals.Apply(p.mesh.vertices, p.origN, p.l2w.inverse.MultiplyPoint3x4(centerW), 1f, p.weights);
                    cleaned += p.headCount;
                }

                // ---- 기준점과 바운드
                var srcs = new List<FaceLandmarks.Src>();
                bool hInit = false, bInit = false;
                foreach (var p in parts)
                {
                    var v = p.mesh.vertices; var wv = new Vector3[v.Length]; var wn = new Vector3[v.Length];
                    var nm = p.l2w.inverse.transpose;
                    for (int i = 0; i < v.Length; i++)
                    {
                        wv[i] = p.l2w.MultiplyPoint3x4(v[i]);
                        wn[i] = nm.MultiplyVector(p.origN[i]).normalized;
                        if (!bInit) { rep.body = new Bounds(wv[i], Vector3.zero); bInit = true; } else rep.body.Encapsulate(wv[i]);
                        if (p.boundsW[i] > 0f) { if (!hInit) { rep.head = new Bounds(wv[i], Vector3.zero); hInit = true; } else rep.head.Encapsulate(wv[i]); }
                    }
                    srcs.Add(new FaceLandmarks.Src { name = p.name, world = wv, weights = p.weights, isFace = p.isFace, normals = wn, triangles = p.mesh.triangles });
                }
                var marks = FaceLandmarks.Detect(srcs);
                rep.faceFound = marks.faceFound && hInit;
                if (!hInit) rep.head = rep.body;
                if (rep.faceFound)
                {
                    marks.CalibrateNoseStop(srcs, DefaultLight());
                    marks.FitNoseMoon(srcs, DefaultLight(), MoonThicknessDefault, 0, false, 1f, MoonBulgeDefault, 1f, DropRadiusDefault, DropHeightDefault);
                    rep.lines.Add("얼굴 기준점: " + marks.Describe());
                    rep.lines.Add(marks.noseStopNote + " / " + marks.moonNote);
                }
                else rep.lines.Add("얼굴 기준점을 찾지 못함: 머리 노멀 정리는 하되, 명암선·코 음영 없이 모든 메시에 일반 툰 밴드 재질을 입힙니다. (머리 본 이름·얼굴 재질 규칙을 모델 설정에서 확인)");
                rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "노멀 정리: 얼굴 정점 {0}개, 중심 {1}", cleaned, centerW));

                // ---- 1.5) 얼굴 파싱: 렌더한 머리에서 피부·눈썹·눈·코·입술·귀·목·머리카락 라벨을 얻어 UV 로 옮긴다 (기록 P 절).
                //      원본 프리팹을 따로 세워 비교 렌더와 같은 보정(알파 컷아웃·끊긴 텍스처 연결)을 한 뒤 찍는다. 변환 중인 inst 는 그대로 둔다.
                var labelsOf = new Dictionary<Texture, FBX2NPRParseBake.TexResult>();
                if (crit.useFaceParser && crit.flattenFaceSkin && rep.faceFound)
                {
                    var plog = new List<string>();
                    using (var parser = FaceParser.Load(plog))
                    {
                        if (parser == null) foreach (var l in plog) rep.lines.Add("얼굴 파싱 없이 진행: " + l);
                        else
                        {
                            var pgo = (GameObject)PrefabUtility.InstantiatePrefab(prefab); var ptemps = new List<UnityEngine.Object>();
                            try
                            {
                                FBX2NPRCompare.FixOriginal(pgo, crit, modelPath, ptemps);
                                var pr = FBX2NPRParseBake.Bake(pgo, crit, modelPath, parser, 1024, null, string.IsNullOrEmpty(DebugDir) ? null : DebugDir, Safe(c));
                                foreach (var l in pr.log) rep.lines.Add("  얼굴 파싱 · " + l.Trim());
                                foreach (var t in pr.textures) labelsOf[t.albedo] = t;
                            }
                            catch (Exception ex) { rep.lines.Add("얼굴 파싱 실패 (예전 방식으로 진행): " + ex.Message); labelsOf.Clear(); }
                            finally { UnityEngine.Object.DestroyImmediate(pgo); foreach (var t in ptemps) if (t != null) UnityEngine.Object.DestroyImmediate(t); }
                        }
                    }
                }

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // 이번 실행에서 쓴 에셋 경로 (끝나고 남은 것을 정리)

                // ---- 2) 얼굴 텍스처 평탄화. 텍스처마다 한 번, 같은 텍스처를 쓰는 메시는 전부 넘긴다(가중치 0 이면 덮임 판정에만 쓰인다).
                var flatOf = new Dictionary<Texture, Texture2D>();
                // 얼굴 밖까지 평탄화했을 때, 실제로 텍셀이 바뀐 메시의 원본 재질. 이 재질만 평탄화본을 쓴다.
                // (바뀌지 않은 옷 재질까지 평탄화본으로 돌리면, 압축된 임포트본을 읽어 다시 저장한 사본이라 옷이 미세하게 달라진다.)
                var bodyChanged = new HashSet<Material>();
                var faceBlocked = new HashSet<Texture>();  // 얼굴 평탄화가 '건드리면 안 된다'고 판정한 텍스처(이미 평평함, 피부가 한 색이 아님): 몸도 건드리지 않는다
                var texNames = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);   // 저장 이름 → 텍스처 (이름이 같은 다른 텍스처가 덮어쓰지 않게)
                bool haveFill = false; var faceFill = new Color32(0, 0, 0, 255);
                float ipd = marks.eyesFound ? (marks.eyeA - marks.eyeB).magnitude : 0.34f * marks.face.size.x;
                int baked = 0, alreadyFlat = 0, bodyBaked = 0;
                if (!crit.flattenFaceSkin) rep.lines.Add("텍스처 평탄화: 모델 설정에서 꺼져 있어 건너뜀 (flattenFaceSkin = false)");
                else if (rep.faceFound && !marks.noseFound) rep.lines.Add("텍스처 평탄화: 코를 찾지 못해 건너뜀 (피부 표본을 코 주변에서 뜬다)");
                if (rep.faceFound && marks.noseFound && crit.flattenFaceSkin)
                {
                    var seen = new HashSet<Texture>();     // 얼굴 평탄화를 시도한 텍스처
                    var done = new HashSet<Texture>();     // 평탄화본이 정해진 텍스처 (몸 단계에서 다시 하지 않는다)
                    foreach (var p in parts)
                    {
                        if (!p.FaceRend || p.albedo == null || !seen.Add(p.albedo)) continue;
                        var fsrc = new List<SkinFlatBake.Src>(); var tmpMeshes = new List<Mesh>();
                        foreach (var q in parts)
                        {
                            Mesh fm = MeshForTexture(q, p.albedo, tmpMeshes); if (fm == null) continue;
                            fsrc.Add(new SkinFlatBake.Src { name = q.name, mesh = fm, l2w = q.l2w, weights = q.weights,
                                                             // 라벨은 피부 메시에만. 옷 메시(Ch07 셔츠 깃은 재질이 피부와 같아 skinRule 이 참)에 '목' 라벨이 번지면 깃이 피부색이 된다(실측)
                                                             labelOk = (q.FaceRend || q.skinRule) && !crit.IsBodyFlattenSkipped(q.name) && !crit.IsClothMesh(q.name) });
                        }
                        // 라벨이 '어디'를 정하는 것은 파서가 이 텍스처에서 눈·눈썹을 찾았을 때뿐이다 (Ch07 눈 1.7%/눈썹 0.6%, The Boss 0/0 — 78번).
                        // 눈·눈썹을 못 찾은 얼굴(손그림 The Boss)은 나머지 라벨도 믿을 수 없어 — 라벨 절반·색 무리 절반으로 칠하면 수염이 조각난다(81번) — 예전 방식 그대로 간다.
                        SkinFlatBake.Labels lab = null; FBX2NPRParseBake.TexResult tr; string labelSkip = null;
                        if (labelsOf.TryGetValue(p.albedo, out tr) && tr.observed > 0)
                        {
                            int eyes = tr.classCount[FaceParser.LEye] + tr.classCount[FaceParser.REye], brows = tr.classCount[FaceParser.LBrow] + tr.classCount[FaceParser.RBrow];
                            bool trust = eyes >= ParserEyeMinFrac * tr.observed && brows >= ParserBrowMinFrac * tr.observed;
                            if (trust) lab = new SkinFlatBake.Labels { W = tr.W, H = tr.H, label = tr.label, filled = tr.filled, trustFeatures = true, draw = crit.drawFeatures ? new FeatureDrawCore.Opt { lipBottomExtend = crit.toonLipBottomExtend, lipTipExtend = crit.toonLipTipExtend } : null };
                            else labelSkip = string.Format(CultureInfo.InvariantCulture, "라벨 안 씀(파서가 눈 {0:F2}%·눈썹 {1:F2}% 로 못 찾음, 기준 {2:F1}%·{3:F1}%) — 예전 방식(머리 본 가중치 + 색 통계). ",
                                                           100f * eyes / tr.observed, 100f * brows / tr.observed, 100f * ParserEyeMinFrac, 100f * ParserBrowMinFrac);
                        }
                        var r = SkinFlatBake.Bake(p.albedo, fsrc, marks, new SkinFlatCore.Opt(), false, lab);
                        if (lab != null) r.note = r.labelNote + r.note; else if (labelSkip != null) r.note = labelSkip + r.note;
                        if (r.drawn != null)
                        {
                            rep.lines.Add("  이목구비 그리기: 지운 텍셀 " + r.drawn.erasedPx + ", 눈썹 " + r.drawn.browPx + ", 눈매 " + r.drawn.lashPx + ", 콧구멍 " + r.drawn.nostrilN + "개, 입술 " + r.drawn.lipPx + " (얼굴 위 = " + (r.drawn.upIsPlusY ? "+y" : "-y") + ")");
                            foreach (var dl in r.drawn.log) rep.lines.Add("    " + dl);
                            if (r.drawn.lashTraced > 0) lashDrawn = true;
                        }
                        foreach (var tm in tmpMeshes) UnityEngine.Object.DestroyImmediate(tm);
                        var k = r.core;
                        string texBase = TexBase(dir, p.albedo, texNames);
                        float gap = (k != null && r.albedo != null) ? ChromaGap(k.flatR, k.flatG, k.flatB, k.tgtR, k.tgtG, k.tgtB) : 0f;
                        bool blocked = r.albedo != null && gap > FlatColorGapMax && !crit.flattenIgnoreColorCheck;
                        if (AssetDatabase.LoadAssetAtPath<Texture2D>(texBase + "_flatmask.png") != null) AssetDatabase.DeleteAsset(texBase + "_flatmask.png");   // 예전 실행의 디버그 마스크
                        if (r.mask != null && !string.IsNullOrEmpty(DebugDir))
                            File.WriteAllBytes(Path.Combine(DebugDir, Safe(c) + "_" + Safe(p.albedo.name) + "_flatmask" + (blocked ? "_blocked" : "") + ".png"), r.mask.EncodeToPNG());
                        if (r.albedo != null && !blocked)
                        {
                            // 렌더 공간 아랫입술 대칭 보완 (모델 설정 toonLipSymmetrySide). 몸 평탄화 전에 해서 보탠 텍셀이 "끝난 곳"으로 보호되게
                            if (crit.toonLipSymmetrySide != 0 && r.drawn != null) { var lsn = FBX2NPRLipSymmetry.Apply(inst, rep.head, p.albedo, r, crit, modelPath); if (lsn != null) rep.lines.Add("  " + lsn); }
                            Texture2D final = r.albedo; RegionFlatBake.Result rb = null;
                            var fillHere = new Color32(k.flatR, k.flatG, k.flatB, 255);
                            if (!haveFill) { haveFill = true; faceFill = fillHere; }
                            if (bodyMode > 0)
                            {
                                // 얼굴 밖(목·손·팔, 옷)도 같은 수준으로 단순화한다. 얼굴 평탄화본에서 이어서 하고, 피부는 얼굴의 평탄색으로 칠한다.
                                var rtmp = new List<Mesh>(); var rparts = new List<Part>();
                                var rsrc = RegionSources(parts, p.albedo, crit, rtmp, rparts, true);
                                rb = RegionFlatBake.Bake(r.albedo, p.albedo, rsrc, ipd, true, fillHere, RegionOpt(bodyMode), r.labelDone);
                                foreach (var tm in rtmp) UnityEngine.Object.DestroyImmediate(tm);
                                foreach (var line in rb.log) rep.lines.Add("  몸 평탄화 · " + line);
                                if (rb.albedo != null) { final = rb.albedo; bodyBaked++; NoteChanged(rsrc, rparts, p.albedo, bodyChanged); }
                                if (rb.mask != null && !string.IsNullOrEmpty(DebugDir))
                                    File.WriteAllBytes(Path.Combine(DebugDir, Safe(c) + "_" + Safe(p.albedo.name) + "_bodymask.png"), rb.mask.EncodeToPNG());
                            }
                            flatOf[p.albedo] = SaveTexture(final.EncodeToPNG(), texBase + "_flat.png", p.albedo);
                            used.Add(texBase + "_flat.png");
                            baked++; done.Add(p.albedo);
                            if (rb != null) foreach (var t in new UnityEngine.Object[] { rb.albedo, rb.mask }) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                        }
                        else
                        {
                            if (AssetDatabase.LoadAssetAtPath<Texture2D>(texBase + "_flat.png") != null) AssetDatabase.DeleteAsset(texBase + "_flat.png");
                            if (blocked) { rep.flattenBlocked = true; faceBlocked.Add(p.albedo); }
                            else if (k != null && k.note.Contains("이미 평평함")) { alreadyFlat++; faceBlocked.Add(p.albedo); }
                            // 그 밖의 건너뜀(표본 없음 등: 얼굴 정점이 조금 걸쳤을 뿐 얼굴 텍스처가 아닌 경우)은 아래 몸 단계에서 다시 본다
                        }
                        string what = blocked ? "그대로 씀 — 피부가 한 색이 아님 (평탄색과 원래 평균색의 색 차이가 기준보다 큼. 억지로 하려면 모델 설정 flattenIgnoreColorCheck)"
                                    : r.Skipped ? "그대로 씀 — " + (k != null ? k.note : "") : "피부 평탄화본 저장";
                        // 색 차이는 평탄화가 실제로 돈 텍스처에서만 잰다 (건너뛴 텍스처는 '-')
                        string gapText = (k != null && r.albedo != null) ? gap.ToString("F3", CultureInfo.InvariantCulture) : "-";
                        rep.lines.Add(string.Format(CultureInfo.InvariantCulture,
                            "텍스처 {0}: {1} (잔무늬 {2:F2}, 평탄색 ({3},{4},{5}), 대상 평균색 ({6:F0},{7:F0},{8:F0}), 색 차이 {9} / 기준 {10:F3}, 남긴 비율 {11:F1}%) {12}",
                            p.albedo.name, what, k != null ? k.gateFine : 0f,
                            k != null ? k.flatR : 0, k != null ? k.flatG : 0, k != null ? k.flatB : 0,
                            k != null ? k.tgtR : 0f, k != null ? k.tgtG : 0f, k != null ? k.tgtB : 0f, gapText, FlatColorGapMax, k != null ? k.keepPct : 0f, r.note));
                        foreach (var t in new UnityEngine.Object[] { r.albedo, r.mask, r.copy }) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                    }
                    // 얼굴이 없는 텍스처(옷만 따로 쓰는 텍스처 등)도 같은 방식으로. 얼굴 평탄화가 하나라도 됐을 때만 한다:
                    // 얼굴을 건너뛴 캐릭터(이미 평평하거나 피부가 한 색이 아님)는 몸도 건드리지 않는다.
                    if (bodyMode > 0 && haveFill)
                    {
                        foreach (var p in parts)
                            foreach (var tex in p.slotAlbedo)
                            {
                                if (tex == null || faceBlocked.Contains(tex) || !done.Add(tex)) continue;
                                // 이 텍스처에서는 얼굴 평탄화가 아무것도 하지 않았으므로 '끝난 곳'이 없다 (얼굴 렌더러로 넘기지 않는다)
                                var rtmp = new List<Mesh>(); var rparts = new List<Part>();
                                var rsrc = RegionSources(parts, tex, crit, rtmp, rparts, false);
                                if (!rsrc.Exists(x => !x.skip)) { foreach (var tm in rtmp) UnityEngine.Object.DestroyImmediate(tm); continue; }
                                var rb = RegionFlatBake.Bake(tex, tex, rsrc, ipd, true, faceFill, RegionOpt(bodyMode));
                                foreach (var tm in rtmp) UnityEngine.Object.DestroyImmediate(tm);
                                foreach (var line in rb.log) rep.lines.Add("  몸 평탄화 · " + tex.name + " · " + line);
                                string tb = TexBase(dir, tex, texNames);
                                if (rb.albedo != null)
                                {
                                    flatOf[tex] = SaveTexture(rb.albedo.EncodeToPNG(), tb + "_flat.png", tex); used.Add(tb + "_flat.png"); bodyBaked++; NoteChanged(rsrc, rparts, tex, bodyChanged);
                                    if (rb.mask != null && !string.IsNullOrEmpty(DebugDir)) File.WriteAllBytes(Path.Combine(DebugDir, Safe(c) + "_" + Safe(tex.name) + "_bodymask.png"), rb.mask.EncodeToPNG());
                                }
                                foreach (var t in new UnityEngine.Object[] { rb.albedo, rb.mask }) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                            }
                    }
                    rep.lines.Add("몸 평탄화: " + (bodyMode == 0 ? "끔 (얼굴만)"
                        : !haveFill ? "하지 않음 — 얼굴 평탄화를 건너뛴 캐릭터는 몸도 건드리지 않는다"
                        : (bodyMode == 1 ? "피부까지 (목·손·팔을 얼굴과 같은 색으로)" : "옷까지 (색 무리마다 평평한 색면으로)") + ", 얼굴 밖을 고친 텍스처 " + bodyBaked + "개"));
                }
                // 손으로 평평하게 칠한 얼굴(평탄화가 건너뛴 경우)은 얼굴 안쪽 선이 중복이라 실루엣 선만 쓴다 (규칙 9, 34번).
                rep.handPainted = baked == 0 && alreadyFlat > 0;
                if (rep.handPainted) rep.lines.Add("얼굴 텍스처가 이미 평평함: 얼굴도 실루엣 선만 씁니다.");

                // ---- 3) 메시·재질 에셋과 할당. 같은 경로에 이미 있으면 내용만 바꿔 식별자(GUID)를 지킨다.
                var shader = Shader.Find("NormalTest/ToonBand");
                if (shader == null) { rep.lines.Add("셰이더 NormalTest/ToonBand 없음"); return rep; }
                var matOf = new Dictionary<string, Material>();
                var faceRends = new List<Renderer>(); var otherRends = new List<Renderer>();
                int matN = 0;
                var matNotes = new List<string>();
                foreach (var p in parts)
                {
                    string meshPath = UniquePath(dir + "/Meshes/" + Safe(p.name), ".asset", used);
                    p.mesh = SaveOrReplace(p.mesh, meshPath);

                    bool faceRend = p.FaceRend && rep.faceFound;   // 얼굴을 못 찾았으면 얼굴 재질을 쓰지 않는다 (기준점이 없어 명암선을 그릴 수 없다)
                    var mats = new Material[Mathf.Max(1, p.srcMats.Length)];
                    for (int mi = 0; mi < mats.Length; mi++)
                    {
                        var src = mi < p.srcMats.Length ? p.srcMats[mi] : null;
                        bool face = faceRend && p.faceSlot[Mathf.Min(mi, p.faceSlot.Length - 1)];   // 재질 칸마다: 얼굴 메시 안의 속눈썹 카드 등은 일반 재질
                        string key = (src != null ? src.GetInstanceID().ToString() : "none") + (face ? "_face" : "_other");
                        Material m;
                        if (!matOf.TryGetValue(key, out m))
                        {
                            Texture tex = ResolveAlbedo(src, modelPath, crit);
                            Texture2D flat;
                            if (tex != null && flatOf.TryGetValue(tex, out flat) && (face || (src != null && bodyChanged.Contains(src)))) tex = flat;
                            m = new Material(shader);
                            SetupMat(m, face, tex, rep.handPainted, rep.head.center, src, matNotes);
                            string matPath = UniquePath(dir + "/Materials/" + Safe(src != null ? src.name : "none") + (face ? "_face" : ""), ".mat", used);
                            m = SaveOrReplace(m, matPath);
                            matOf[key] = m; matN++;
                        }
                        mats[mi] = m;
                    }
                    if (p.smr != null) p.smr.sharedMesh = p.mesh; else p.mf.sharedMesh = p.mesh;
                    p.rend.sharedMaterials = mats;
                    (faceRend ? faceRends : otherRends).Add(p.rend);
                }
                rep.lines.Add(string.Format("메시 {0}개, 재질 {1}개 (얼굴 렌더러 {2}, 그 밖 {3}), 텍스처 평탄화 {4}개", parts.Count, matN, faceRends.Count, otherRends.Count, baked));
                foreach (var n in matNotes) rep.lines.Add(n);

                // ---- 속눈썹 카드 메시: 아이라인(먹선)을 그렸으면 숨긴다. 사진 속눈썹 가닥(알파 컷아웃)이 눈 둘레에 점점이 남아 "잔여 사진 픽셀"로 보였다 (기록 95번).
                if (lashDrawn && !string.IsNullOrEmpty(crit.toonHideMeshContains))
                {
                    foreach (var rr in inst.GetComponentsInChildren<Renderer>(true))
                    {
                        var smr = rr as SkinnedMeshRenderer; string meshName = smr != null && smr.sharedMesh != null ? smr.sharedMesh.name : (rr.GetComponent<MeshFilter>() != null && rr.GetComponent<MeshFilter>().sharedMesh != null ? rr.GetComponent<MeshFilter>().sharedMesh.name : "");
                        if (!rr.enabled || !(crit.IsToonHiddenMesh(rr.name) || crit.IsToonHiddenMesh(meshName))) continue;
                        rr.enabled = false; rep.lines.Add("  숨김: " + rr.name + " (속눈썹 카드 메시 — 아이라인을 그렸으므로, 설정 toonHideMeshContains)");
                    }
                }

                // ---- 얼굴 리그: 머리 본 아래에 두어 머리와 같이 움직이게 한다
                var rigGo = new GameObject("툰 얼굴 리그");
                rigGo.transform.SetParent(headBone != null ? headBone : inst.transform, false);
                rigGo.transform.position = rep.head.center; rigGo.transform.rotation = Quaternion.identity;   // 변환할 때 자세에서 앞 +Z, 위 +Y
                SetWorldScaleOne(rigGo.transform);
                var rig = rigGo.AddComponent<ToonFaceRig>();
                rig.faceRenderers = faceRends; rig.otherRenderers = otherRends; rig.sourceModelGuid = modelGuid;
                rig.generatedAssets = new List<string>();
                foreach (var ap in used) { string g = AssetDatabase.AssetPathToGUID(ap); if (!string.IsNullOrEmpty(g)) rig.generatedAssets.Add(g); }
                rig.lightTransform = null;   // 씬의 디렉셔널 라이트를 따른다
                rig.faceCenter = Marker(rigGo.transform, "얼굴 중심", rep.head.center);   // 머리 본에 묶인 메시(머리카락 포함)의 바운드 중심. 미리보기와 같은 기준
                if (rep.faceFound)
                {
                    if (marks.noseFound) rig.noseTip = Marker(rigGo.transform, "코끝", marks.noseTip);
                    rig.noseRadius = marks.noseRadius;
                    if (marks.eyesFound)
                    {
                        rig.eyeA = Marker(rigGo.transform, "눈 A", marks.eyeA); rig.eyeB = Marker(rigGo.transform, "눈 B", marks.eyeB);
                        rig.eyeRadius = new Vector3(marks.eyeRadius.x, marks.eyeRadius.y, marks.eyeRadius.z);
                    }
                    rig.avoidEyes = false;
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
                }
                else rig.noseShadow = false;
                // 외곽선 값은 리그가 들고 다닌다. 변환할 때는 렌더러 피처에 쓰지 않고, 리그가 켜질 때와 값을 바꿀 때 로드된 피처에 넣는다 (ToonFaceRig.ApplyOutline).
                var feat = FindFeature();
                if (feat != null)
                {
                    rig.outlineFeature = feat; rig.outline = true; rig.driveOutlineSettings = true;
                    rig.outlineDarkness = 0.35f; rig.outlineThickness = 1.0f; rig.outlineDepthScale = 12f; rig.outlineNormalScale = 0.8f; rig.outlineThreshold = 0.30f;
                }
                else rep.lines.Add("외곽선 피처를 찾지 못함 (" + RendererPath + "): 선 없이 저장합니다.");
                if (rep.faceFound && !rep.handPainted)
                {
                    float fh = Mathf.Max(marks.face.size.y, 0.05f);
                    rig.innerLineFade = new Vector2(fh / InnerLineFadeStartPx, fh / InnerLineFadeEndPx);
                    rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "얼굴 안쪽 선: 얼굴 메시의 머리 영역 높이({0:F3}m)가 화면에서 {1:F0}픽셀보다 작아지면 얼굴 정점의 안쪽 선을 지우기 시작해 {2:F0}픽셀에서 다 지움 (몸의 선은 그대로)", fh, InnerLineFadeStartPx, InnerLineFadeEndPx));
                }
                rep.lines.Add("리그 위치: " + (headBone != null ? "머리 본 '" + headBone.name + "' 아래" : "머리 본을 못 찾아 루트 아래 (머리를 따라 움직이지 않음)"));

                // ---- 애니메이션: 원본 모델에 Animator 가 없으면 붙여 둔다 (컨트롤러만 넣으면 재생된다)
                // 모델에 Animator 는 있는데 아바타가 비어 있으면(Generic + 아바타 없음으로 임포트했던 경우) 모델의 아바타를 넣는다 — Humanoid 로 바꾼 뒤에도 프리팹이 클립을 받게 (기록 97번)
                { var anExisting = inst.GetComponent<Animator>(); if (anExisting != null && anExisting.avatar == null) { foreach (var o in AssetDatabase.LoadAllAssetsAtPath(modelPath)) { var av = o as Avatar; if (av != null) { anExisting.avatar = av; break; } } } if (anExisting != null) anExisting.applyRootMotion = false; }
                if (inst.GetComponent<Animator>() == null && inst.GetComponent<Animation>() == null)
                {
                    var an = inst.AddComponent<Animator>();
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(modelPath)) { var av = o as Avatar; if (av != null) { an.avatar = av; break; } }
                    rep.lines.Add("Animator 추가: " + (an.avatar != null ? "아바타 '" + an.avatar.name + "'" : "아바타 없음 (원본 모델이 아바타를 만들지 않음. 클립은 본 경로로 연결된다 — Animator 로 재생하는 것은 시험하지 않음)"));
                }

                bool ok;
                PrefabUtility.SaveAsPrefabAsset(inst, prefabPath, out ok);   // 같은 경로면 덮어쓴다 (GUID 유지)
                if (!ok) { rep.lines.Add("프리팹 저장 실패: " + prefabPath); return rep; }
                rep.prefabPath = prefabPath;
                rep.lines.Add("프리팹: " + prefabPath + (existed ? " (기존 프리팹을 덮어씀. 리그 값은 변환 도구 기본값으로 돌아갑니다)" : ""));

                // ---- 저장에 성공한 뒤에만, 이번에 쓰지 않은 예전 메시·재질·평탄화 텍스처를 정리한다
                // 지난 변환이 리그에 적어 둔 목록(GUID)에 있는 에셋만 지운다. 사용자가 같은 폴더에 만들거나 복제해 둔 것은 목록에 없으므로 건드리지 않는다.
                // (라벨로 가리면 안 된다: 에셋을 복제하면 라벨도 따라가서 사용자의 사본이 지워진다. 라벨은 찾아보기용 표시일 뿐이다.)
                var removed = new List<string>();
                foreach (var g in prevGenerated)
                {
                    var ap = AssetDatabase.GUIDToAssetPath(g);
                    if (string.IsNullOrEmpty(ap) || used.Contains(ap) || !ap.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    removed.Add(ap);
                }
                foreach (var ap in removed) AssetDatabase.DeleteAsset(ap);
                if (removed.Count > 0) rep.lines.Add("예전 실행의 쓰지 않는 에셋 " + removed.Count + "개 정리: " + string.Join(", ", removed.ConvertAll(x => Path.GetFileName(x)).ToArray()));
            }
            finally { UnityEngine.Object.DestroyImmediate(inst); }
            return rep;
        }

        // ---------- 재질 (미리보기 생성기 SetupMat 과 같은 값 + 탄젠트의 원본 노멀) ----------
        static void SetupMat(Material mat, bool isFace, Texture albedo, bool silFace, Vector3 faceCenter, Material src, List<string> notes)
        {
            mat.SetVector("_Band0", new Vector4(0.32f, 0.30f, 0.42f, 1f));
            mat.SetVector("_Band1", new Vector4(0.72f, 0.66f, 0.70f, 1f));
            mat.SetVector("_Band2", new Vector4(1.00f, 0.96f, 0.92f, 1f));
            mat.SetFloat("_Stop1", 0.0f); mat.SetFloat("_Stop2", 0.35f);   // 얼굴이 아닌 메시는 2톤 밴드. 경계는 리그의 bodyShadow 기본값과 같게 (리그가 꺼져 있어도 같은 모습)
            mat.SetFloat("_UseOrigNormal", 1f); mat.SetFloat("_NormalStrength", 0f);
            mat.SetFloat("_OrigFromTangent", 1f);                          // 스킨 메시: 원본 노멀이 뼈를 따라가도록
            mat.SetFloat("_OutlineFlatNormal", (silFace || !isFace) ? 1f : 0f);
            if (albedo != null) mat.SetTexture("_Albedo", albedo);
            mat.SetFloat("_AlbedoOn", albedo != null ? 1f : 0f);
            // 컷아웃은 텍스처에 실제로 알파가 있을 때만 (머리카락·속눈썹 카드). 알파에 다른 값(매끈함 등)을 넣은 텍스처는 구분하지 못한다.
            mat.SetFloat("_Cutout", (!isFace && albedo != null && HasAlpha(albedo)) ? 1f : 0f); mat.SetFloat("_Cutoff", 0.5f);
            mat.SetFloat("_Posterize", 0f); mat.SetFloat("_PosterizeLum", 0f);
            mat.SetFloat("_Flatten", 0f); mat.SetFloat("_Saturation", 1f); mat.SetFloat("_Lift", 0f);
            mat.SetVector("_LightDir", DefaultLight());
            mat.SetFloat("_FaceWeightOn", isFace ? 1f : 0f);                // 얼굴과 몸이 한 메시여도 얼굴 명암은 얼굴에만
            if (isFace)
            {
                mat.SetVector("_FaceCenter", faceCenter);
                mat.SetFloat("_FaceFlat", 0f); mat.SetFloat("_EdgeDark", 0f); mat.SetFloat("_OrigBlend", 0f);
                mat.SetFloat("_EdgeWidth", 0.95f); mat.SetFloat("_EdgeTaper", 0.15f); mat.SetFloat("_EdgeSoft", 0.04f);
                mat.SetFloat("_RimWidth", 0.92f);
            }
            if (src == null) return;
            // 발광 맵은 옮긴다 (보석·불빛). 그 밖의 맵(노멀·매끈함·금속)은 툰 셰이더가 쓰지 않는다.
            if (src.HasProperty("_EmissionMap") && src.IsKeywordEnabled("_EMISSION"))
            {
                var em = src.GetTexture("_EmissionMap"); var ec = src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white;
                if (em != null && ec.maxColorComponent > 0f)
                {
                    mat.SetTexture("_EmissionMap", em); mat.SetVector("_EmissionColor", new Vector4(ec.r, ec.g, ec.b, 0f)); mat.SetFloat("_EmissionOn", 1f);
                    string n = "재질 " + src.name + ": 발광 맵 " + em.name + " 을 옮김"; if (!notes.Contains(n)) notes.Add(n);
                }
                else if (em == null && ec.maxColorComponent > 0f)
                {
                    string n = "주의: 재질 " + src.name + " 의 발광은 맵 없이 색만 있어 옮기지 않습니다."; if (!notes.Contains(n)) notes.Add(n);
                }
            }
            if (src.HasProperty("_BaseColor"))
            {
                var bc = src.GetColor("_BaseColor");
                if (Mathf.Abs(bc.r - 1f) + Mathf.Abs(bc.g - 1f) + Mathf.Abs(bc.b - 1f) > 0.02f)
                {
                    string n = string.Format(CultureInfo.InvariantCulture, "주의: 재질 {0} 의 기본 색 ({1:F2}, {2:F2}, {3:F2}) 은 옮기지 않습니다 (텍스처만 옮김).", src.name, bc.r, bc.g, bc.b);
                    if (!notes.Contains(n)) notes.Add(n);
                }
            }
        }

        // 렌더러를 뺀 뒤 아무것도 남지 않은 오브젝트(자식 없음, Transform 만)는 같이 지운다.
        static void DestroyIfEmpty(GameObject go, HashSet<Transform> bones)
        {
            if (go == null || go.transform.childCount != 0 || go.GetComponents<Component>().Length > 1) return;
            if (bones != null && bones.Contains(go.transform)) return;   // 다른 스킨 메시가 본으로 쓰는 오브젝트는 남긴다
            UnityEngine.Object.DestroyImmediate(go);
        }

        public static bool HasAlpha(Texture t)
        {
            var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t)) as TextureImporter;
            if (ti != null) return ti.DoesSourceTextureHaveAlpha();
            return UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(t.graphicsFormat);   // 임포터가 없는 텍스처(FBX 내장 등)는 포맷으로 판단
        }

        /// <summary>밝기를 뺀 색(색도) 거리. r = R/(R+G+B), g = G/(R+G+B) 평면에서 잰다.</summary>
        static float ChromaGap(float r1, float g1, float b1, float r2, float g2, float b2)
        {
            float s1 = Mathf.Max(r1 + g1 + b1, 1e-3f), s2 = Mathf.Max(r2 + g2 + b2, 1e-3f);
            float dr = r1 / s1 - r2 / s2, dg = g1 / s1 - g2 / s2;
            return Mathf.Sqrt(dr * dr + dg * dg);
        }

        /// <summary>
        /// 안쪽 선 지우기 가중치(UV2.y): 얼굴 재질을 받는 메시(피부 메시)의 정점 전부 1. 얼굴 재질이 아닌 칸·메시는 0 (SetupSlots·CollectParts 가 지운다).
        /// 게임 거리에서는 얼굴뿐 아니라 목·손·발목처럼 작게 보이는 피부 전부가 원본 노멀의 굴곡 때문에 노멀 엣지로 잡혀 선 색 덩어리가 된다
        /// (Ch07 툰, 2026-10-14: 외곽선 켬/끔 평균 밝기 목 86/130, 손 80~88/130~138, 발목 89/145). 처음에는 머리+목(+가슴 윗본) 본 가중치로 부위를 하나씩 덮었지만
        /// 손·발이 또 남아 부위를 고르지 않고 피부 메시 전체로 정했다. 얼굴보다 두 배 먼 거리부터 지우므로(ToonBand) 클로즈업은 그대로다.
        /// 얼굴 가중치(UV2.x)는 얼굴 명암용이라 그대로 둔다. 주의: 갑옷이 피부와 한 메시인 모델(Jones)은 먼 거리에서 갑옷 안쪽 선도 지워진다.
        /// </summary>
        internal static float[] LineFadeWeights(int vertexCount)
        {
            var o = new float[vertexCount];
            for (int i = 0; i < vertexCount; i++) o[i] = 1f;
            return o;
        }

        static List<Part> CollectParts(GameObject inst, FBX2NPRAssetSettings crit, string path, Report rep, Transform headBone)
        {
            var parts = new List<Part>();
            string headKey = (crit.headBoneContains ?? "").ToLowerInvariant();
            float maxBindDiff = 0f; string maxBindName = null;
            var boneSet = new HashSet<Transform>();
            foreach (var s0 in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (s0.rootBone != null) boneSet.Add(s0.rootBone);
                foreach (var bt in s0.bones) if (bt != null) boneSet.Add(bt);
            }
            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var src = smr.sharedMesh; if (src == null) continue;
                // 데칼 판은 프리팹에서 뺀다. 꺼 두기만 하면 프리팹이 원본 FBX 의 메시·재질을 계속 붙들고 있다.
                if (crit.IsIgnoredMesh(src.name)) { rep.lines.Add("데칼로 등록된 메시를 뺌: " + src.name); var dgo = smr.gameObject; UnityEngine.Object.DestroyImmediate(smr); DestroyIfEmpty(dgo, boneSet); continue; }
                var p = new Part { name = src.name, rend = smr, smr = smr, srcMats = smr.sharedMaterials };
                p.mesh = UnityEngine.Object.Instantiate(src); p.mesh.name = src.name;
                p.origN = p.mesh.normals;
                if (p.origN == null || p.origN.Length != p.mesh.vertexCount) { p.mesh.RecalculateNormals(); p.origN = p.mesh.normals; rep.lines.Add("노멀이 없는 메시: 노멀을 새로 계산해 변환 — " + src.name); }
                p.albedo = ResolveAlbedo(smr.sharedMaterial, path, crit);
                var heads = new HashSet<int>();
                if (headKey.Length > 0)
                    for (int i = 0; i < smr.bones.Length; i++)
                        if (smr.bones[i] != null && smr.bones[i].name.ToLowerInvariant().Contains(headKey)) heads.Add(i);
                // 메시 좌표 → 월드. 스킨 메시가 실제로 그려지는 자리는 '본 x 바인드 포즈'가 정하고 메시 오브젝트의 트랜스폼은 쓰이지 않는다.
                // 둘이 다른 모델(임포트된 자세가 바인드 포즈와 조금 다르거나, 메시 노드에 스케일이 걸린 FBX)에서는 머리 본 기준 행렬을 쓴다.
                float diff; p.l2w = BindToWorld(smr, src, headBone, out diff); p.bindDiff = diff;
                if (diff > maxBindDiff) { maxBindDiff = diff; maxBindName = src.name; }
                p.weights = new float[p.mesh.vertexCount];
                var bw = src.boneWeights;
                if (bw != null && bw.Length == p.mesh.vertexCount && heads.Count > 0)
                {
                    for (int i = 0; i < bw.Length; i++)
                    {
                        float w = 0f;
                        if (heads.Contains(bw[i].boneIndex0)) w += bw[i].weight0;
                        if (heads.Contains(bw[i].boneIndex1)) w += bw[i].weight1;
                        if (heads.Contains(bw[i].boneIndex2)) w += bw[i].weight2;
                        if (heads.Contains(bw[i].boneIndex3)) w += bw[i].weight3;
                        if (w >= WeightThreshold) { p.weights[i] = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(WeightThreshold, 1f, w)); p.headCount++; }
                    }
                }
                p.boundsW = (float[])p.weights.Clone();
                p.lineFadeW = LineFadeWeights(p.mesh.vertexCount);
                p.isFace = crit.IsFaceMesh(src.name, smr.sharedMaterials); p.skinRule = p.isFace;
                if (!p.isFace) { p.weights = new float[p.mesh.vertexCount]; p.headCount = 0; p.lineFadeW = new float[p.mesh.vertexCount]; }
                SetupSlots(p, crit, path, rep);
                parts.Add(p);
            }
            if (maxBindDiff > 1e-4f)
            {
                float faceDiff = 0f; string faceName = "-";
                foreach (var q in parts) if (q.FaceRend && q.bindDiff >= faceDiff) { faceDiff = q.bindDiff; faceName = q.name; }
                rep.lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "메시 좌표계: 메시 오브젝트의 트랜스폼과 '본 x 바인드 포즈'가 다름 (얼굴 메시 {0}: {1:G3}, 전체에서 가장 큰 차이 {2:G3}: {3}). 차이가 있는 메시는 본 기준 행렬로 옮깁니다 (머리 본이 묶인 메시는 머리 본, 아니면 첫 본).",
                    faceName, faceDiff, maxBindDiff, maxBindName));
            }
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.sharedMesh; if (src == null) continue;
                var mr = mf.GetComponent<MeshRenderer>(); if (mr == null) continue;
                if (crit.IsIgnoredMesh(src.name)) { rep.lines.Add("데칼로 등록된 메시를 뺌: " + src.name); var dgo = mf.gameObject; UnityEngine.Object.DestroyImmediate(mr); UnityEngine.Object.DestroyImmediate(mf); DestroyIfEmpty(dgo, boneSet); continue; }
                var p = new Part { name = src.name, rend = mr, mf = mf, srcMats = mr.sharedMaterials };
                p.mesh = UnityEngine.Object.Instantiate(src); p.mesh.name = src.name;
                p.origN = p.mesh.normals;
                if (p.origN == null || p.origN.Length != p.mesh.vertexCount) { p.mesh.RecalculateNormals(); p.origN = p.mesh.normals; rep.lines.Add("노멀이 없는 메시: 노멀을 새로 계산해 변환 — " + src.name); }
                p.l2w = mf.transform.localToWorldMatrix;
                p.albedo = ResolveAlbedo(p.srcMats.Length > 0 ? p.srcMats[0] : null, path, crit);
                bool underHead = false;
                if (headKey.Length > 0)
                    for (var t = mf.transform; t != null && t != inst.transform; t = t.parent)
                        if (t.name.ToLowerInvariant().Contains(headKey)) { underHead = true; break; }
                p.weights = new float[p.mesh.vertexCount];
                if (underHead) { for (int i = 0; i < p.weights.Length; i++) p.weights[i] = 1f; p.headCount = p.weights.Length; }
                p.boundsW = (float[])p.weights.Clone();
                p.lineFadeW = LineFadeWeights(p.mesh.vertexCount);
                p.isFace = crit.IsFaceMesh(src.name, p.srcMats); p.skinRule = p.isFace;
                if (!p.isFace) { p.weights = new float[p.mesh.vertexCount]; p.headCount = 0; p.lineFadeW = new float[p.mesh.vertexCount]; }
                SetupSlots(p, crit, path, rep);
                parts.Add(p);
            }
            return parts;
        }

        /// <summary>
        /// 스킨 메시의 메시 좌표 → 지금 자세의 월드. 머리 본이 이 메시의 본 목록에 있으면 그 본으로, 없으면 첫 본으로 구한다.
        /// 메시 오브젝트의 트랜스폼과 사실상 같으면(차이 1e-4 이하) 트랜스폼을 그대로 쓴다. diff 는 두 행렬 원소의 가장 큰 차이.
        /// </summary>
        static Matrix4x4 BindToWorld(SkinnedMeshRenderer smr, Mesh mesh, Transform headBone, out float diff)
        {
            var own = smr.transform.localToWorldMatrix; diff = 0f;
            var bones = smr.bones; var bp = mesh.bindposes; int pick = -1;
            int n = Mathf.Min(bones.Length, bp.Length);
            if (headBone != null) for (int i = 0; i < n; i++) if (bones[i] == headBone) { pick = i; break; }
            if (pick < 0) for (int i = 0; i < n; i++) if (bones[i] != null) { pick = i; break; }
            if (pick < 0) return own;
            var m = bones[pick].localToWorldMatrix * bp[pick];
            for (int i = 0; i < 16; i++) diff = Mathf.Max(diff, Mathf.Abs(m[i] - own[i]));
            return diff > 1e-4f ? m : own;
        }

        public static Texture ResolveAlbedo(Material m, string modelPath, FBX2NPRAssetSettings s)
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

        /// <summary>PNG 를 에셋으로 저장하고 임포트 설정을 맞춘다. 같은 경로에 덮어쓰면 GUID 는 그대로다.</summary>
        internal static Texture2D SaveTexture(byte[] png, string assetPath, Texture like)
        {
            File.WriteAllBytes(Path.Combine(Directory.GetParent(Application.dataPath).FullName, assetPath), png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            var ti = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (ti != null)
            {
                ti.textureType = TextureImporterType.Default;
                ti.maxTextureSize = Mathf.Max(2048, Mathf.NextPowerOfTwo(Mathf.Max(like.width, like.height)));
                ti.wrapMode = like.wrapMode;
                ti.alphaIsTransparency = false;
                ti.sRGBTexture = true; ti.mipmapEnabled = true; ti.filterMode = like.filterMode; ti.anisoLevel = like.anisoLevel; ti.textureCompression = TextureImporterCompression.CompressedHQ;
                ti.SaveAndReimport();
            }
            var saved = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            MarkGenerated(saved);
            return saved;
        }

        /// <summary>같은 경로에 에셋이 있으면 내용만 바꾸고(식별자 유지) 그 에셋을 돌려준다. 없으면 새로 만든다.</summary>
        internal static T SaveOrReplace<T>(T obj, string path) where T : UnityEngine.Object
        {
            obj.name = Path.GetFileNameWithoutExtension(path);
            var old = AssetDatabase.LoadAssetAtPath<T>(path);
            if (old != null)
            {
                EditorUtility.CopySerialized(obj, old);
                UnityEngine.Object.DestroyImmediate(obj);
                EditorUtility.SetDirty(old);
                MarkGenerated(old);
                return old;
            }
            AssetDatabase.CreateAsset(obj, path);
            MarkGenerated(obj);
            return obj;
        }

        /// <summary>도구가 만든 에셋에 라벨을 붙인다 (프로젝트 창에서 l:FBX2NPRGenerated 로 찾아보기용. 정리 기준은 아니다).</summary>
        internal static void MarkGenerated(UnityEngine.Object asset)
        {
            if (asset == null) return;
            var labels = new List<string>(AssetDatabase.GetLabels(asset));
            if (!labels.Contains(GeneratedLabel)) { labels.Add(GeneratedLabel); AssetDatabase.SetLabels(asset, labels.ToArray()); }
        }

        /// <summary>
        /// 이 텍스처를 쓰는 재질 칸의 삼각형만 가진 메시 (평탄화의 UV 래스터용). 칸이 하나이거나 모든 칸이 이 텍스처를 쓰면 메시 그대로,
        /// 이 텍스처를 쓰는 칸이 없으면 null. 임시로 만든 메시는 temps 에 넣어 둔다 (쓰고 나서 지울 것).
        /// </summary>
        static Mesh MeshForTexture(Part q, Texture tex, List<Mesh> temps)
        {
            int n = q.slotAlbedo.Length, hit = 0;
            for (int mi = 0; mi < n; mi++) if (q.slotAlbedo[mi] == tex) hit++;
            if (hit == 0) return null;
            if (hit == n) return q.mesh;
            var tm = UnityEngine.Object.Instantiate(q.mesh); var tris = new List<int>();
            int sub = Mathf.Min(n, q.mesh.subMeshCount);
            for (int mi = 0; mi < sub; mi++) if (q.slotAlbedo[mi] == tex) tris.AddRange(q.mesh.GetTriangles(mi));
            tm.subMeshCount = 1; tm.SetTriangles(tris, 0); temps.Add(tm);
            return tm;
        }

        /// <summary>
        /// 재질 칸(서브메시)마다 얼굴 규칙을 다시 본다. 얼굴 메시에 속눈썹 카드 같은 다른 재질 칸이 섞여 있으면
        /// 그 칸의 정점은 얼굴 가중치를 0 으로 해서 노멀 정리·기준점·평탄화에서 빼고, 재질도 일반 재질을 받게 한다.
        /// 재질 칸이 하나인 메시는 예전과 똑같다. (재질 칸이 여럿인 얼굴 메시는 시험 모델이 없어 코드로만 대응했다.)
        /// </summary>
        static void SetupSlots(Part p, FBX2NPRAssetSettings crit, string path, Report rep)
        {
            // 재질 칸은 서브메시가 있는 만큼만 센다 (재질이 서브메시보다 많으면 남는 재질은 마지막 서브메시를 덧그린다: 그 재질은 마지막 칸의 판정을 따른다)
            int n = Mathf.Max(1, Mathf.Min(p.srcMats.Length, p.mesh.subMeshCount));
            p.faceSlot = new bool[n]; p.slotAlbedo = new Texture[n];
            bool anyMat = false; foreach (var mm in p.srcMats) if (mm != null) anyMat = true;
            int faceN = 0;
            for (int mi = 0; mi < n; mi++)
            {
                var m = mi < p.srcMats.Length ? p.srcMats[mi] : null;
                p.slotAlbedo[mi] = ResolveAlbedo(m, path, crit);
                // 재질이 빈 칸은 얼굴로 보지 않는다 (재질이 전부 비어 있는 메시는 예전처럼 이름 규칙만으로)
                p.faceSlot[mi] = p.isFace && (n == 1 || (m != null ? crit.IsFaceMesh(p.name, new[] { m }) : !anyMat));
                if (p.faceSlot[mi]) faceN++;
            }
            if (p.isFace && faceN == 0) { p.isFace = false; p.weights = new float[p.mesh.vertexCount]; p.headCount = 0; p.lineFadeW = new float[p.mesh.vertexCount]; }   // 얼굴 규칙에 맞는 칸이 하나도 없으면 얼굴 메시가 아니다
            if (!p.isFace || faceN == n) return;
            int sub = Mathf.Min(n, p.mesh.subMeshCount);
            var keep = new bool[p.mesh.vertexCount];
            for (int mi = 0; mi < sub; mi++) if (p.faceSlot[mi]) foreach (int idx in p.mesh.GetTriangles(mi)) keep[idx] = true;
            int cnt = 0;
            for (int i = 0; i < keep.Length; i++) { if (!keep[i]) { p.weights[i] = 0f; if (p.lineFadeW != null) p.lineFadeW[i] = 0f; } else if (p.weights[i] > 0f) cnt++; }
            p.headCount = cnt;
            for (int mi = 0; mi < n; mi++) if (p.faceSlot[mi]) { p.albedo = p.slotAlbedo[mi]; break; }
            rep.lines.Add("메시 " + p.name + ": 재질 칸 " + n + "개 중 얼굴 규칙에 맞는 " + faceN + "개만 얼굴로 처리하고 나머지는 일반 재질을 입힙니다. (재질 칸이 여럿인 얼굴 메시는 시험 모델이 없어 코드로만 대응)");
        }

        /// <summary>이 텍스처를 쓰는 메시들을 몸 평탄화의 입력으로 만든다 (그 텍스처를 쓰는 재질 칸의 삼각형만).</summary>
        /// <param name="owners">돌려주는 목록과 같은 순서의 Part (어느 메시가 바뀌었는지 되짚을 때 쓴다)</param>
        /// <param name="faceDone">이 텍스처에서 얼굴 평탄화가 실제로 됐는가. 아니면 얼굴 렌더러도 보통 메시로 넘긴다</param>
        static List<RegionFlatBake.Src> RegionSources(List<Part> parts, Texture tex, FBX2NPRAssetSettings crit, List<Mesh> temps, List<Part> owners, bool faceDone)
        {
            var list = new List<RegionFlatBake.Src>();
            foreach (var q in parts)
            {
                Mesh fm = MeshForTexture(q, tex, temps); if (fm == null) continue;
                list.Add(new RegionFlatBake.Src { name = q.name, mesh = fm, l2w = q.l2w, faceW = q.weights, faceRend = faceDone && q.FaceRend, skinRule = q.skinRule,
                                                  skip = crit.IsBodyFlattenSkipped(q.name), clothName = crit.IsClothMesh(q.name) });
                owners.Add(q);
            }
            return list;
        }

        /// <summary>몸 평탄화에서 텍셀이 바뀐 메시의, 이 텍스처를 쓰는 원본 재질을 적어 둔다 (그 재질만 평탄화본을 쓴다).</summary>
        static void NoteChanged(List<RegionFlatBake.Src> srcs, List<Part> owners, Texture tex, HashSet<Material> changed)
        {
            for (int i = 0; i < srcs.Count && i < owners.Count; i++)
            {
                if (srcs[i].changed <= 0) continue;
                var q = owners[i];
                for (int mi = 0; mi < q.slotAlbedo.Length && mi < q.srcMats.Length; mi++)
                    if (q.slotAlbedo[mi] == tex && q.srcMats[mi] != null) changed.Add(q.srcMats[mi]);
            }
        }

        /// <summary>평탄화본을 저장할 경로(확장자 앞까지). 이름이 같은 다른 텍스처가 있으면 GUID 앞자리를 붙여 서로 덮어쓰지 않게 한다.</summary>
        static string TexBase(string dir, Texture tex, Dictionary<string, Texture> names)
        {
            string n = Safe(tex.name); Texture other;
            if (names.TryGetValue(n, out other) && other != tex)
            {
                string g = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(tex));
                n = n + "_" + (string.IsNullOrEmpty(g) ? Mathf.Abs(tex.GetInstanceID()).ToString() : g.Substring(0, 8));
            }
            names[n] = tex;
            return dir + "/Textures/" + n;
        }

        static RegionFlatCore.Opt RegionOpt(int bodyMode)
        {
            var o = new RegionFlatCore.Opt(); o.skinOnly = bodyMode == 1; return o;
        }

        /// <summary>파일 이름은 메시·재질 이름으로 정한다(실행 순번 없음). 같은 이름이 이번 실행에서 이미 쓰였으면 하이어라키 순서대로 _2, _3 을 붙인다.</summary>
        internal static string UniquePath(string basePath, string ext, HashSet<string> used)
        {
            string p = basePath + ext; int k = 2;
            while (!used.Add(p)) p = basePath + "_" + (k++) + ext;
            return p;
        }

        /// <summary>이름에 머리 본 문자열이 든 트랜스폼 중 가장 얕은 것 (믹사모의 Head 와 HeadTop_End 중 Head).</summary>
        public static Transform FindHeadBone(Transform root, string contains)
        {
            if (string.IsNullOrEmpty(contains)) return null;
            Transform best = null; int bestDepth = int.MaxValue; string key = contains.ToLowerInvariant();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || !t.name.ToLowerInvariant().Contains(key)) continue;
                if (t.GetComponent<Renderer>() != null) continue;   // 이름에 head 가 든 메시 오브젝트(Head_Geo)는 본이 아니다
                int d = 0; for (var q = t; q != null && q != root; q = q.parent) d++;
                if (d < bestDepth) { best = t; bestDepth = d; }
            }
            return best;
        }

        static Transform Marker(Transform parent, string name, Vector3 worldPos)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false); go.transform.position = worldPos; return go.transform;
        }
        // 본에 스케일(예: 0.01)이 걸려 있어도 리그의 월드 스케일은 1 이 되게 한다. 리그는 이 값을 기준으로 프리팹 배율을 읽는다.
        static void SetWorldScaleOne(Transform t)
        {
            var p = t.parent; if (p == null) return;
            var s = p.lossyScale;
            t.localScale = new Vector3(Mathf.Abs(s.x) > 1e-9f ? 1f / s.x : 1f, Mathf.Abs(s.y) > 1e-9f ? 1f / s.y : 1f, Mathf.Abs(s.z) > 1e-9f ? 1f / s.z : 1f);
        }

        public static Vector3 DefaultLight() { return LightDir(35f, 25f); }
        public static Vector3 LightDir(float azDeg, float elDeg)
        {
            float e = elDeg * Mathf.Deg2Rad, a = azDeg * Mathf.Deg2Rad;
            return (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a)) + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a)) + Vector3.up * Mathf.Sin(e)).normalized;
        }

        public static SobelOutlineFeature FindFeature()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd == null) return null;
            foreach (var f in rd.rendererFeatures)
            {
                var s = f as SobelOutlineFeature; if (s == null) continue;
                // 빌드 안전: 셰이더 에셋 참조가 비어 있으면 넣는다. 아무도 참조하지 않는 셰이더는 빌드에서 빠져 플레이어의 Shader.Find 가 null 이 된다 (기록 91번). 저장은 호출한 쪽의 SaveAssets 가 한다.
                if (s.shader == null) { var sh = AssetDatabase.LoadAssetAtPath<Shader>(OutlineShaderPath); if (sh != null) { s.shader = sh; EditorUtility.SetDirty(s); } }
                return s;
            }
            return null;
        }

        public static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        internal static string Safe(string s)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s.Replace(':', '_');
        }
    }
}
