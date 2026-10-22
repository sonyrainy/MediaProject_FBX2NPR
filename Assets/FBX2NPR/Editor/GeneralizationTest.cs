using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 캐릭터 3체에 툰 변환을 "있는 그대로" 적용하고 같은 지표를 잽니다.
    ///
    /// 포스트프로세서와 같은 규칙으로 대상을 고릅니다 (머리 본 가중치 0.5 이상, SMR 전부).
    /// 일부러 예외 처리를 넣지 않습니다. The Boss 의 시가·이·모자가 어떻게 되는지가
    /// 이 실험의 결과 중 하나입니다.
    ///
    /// 조건: mix 0 / 0.5 / 1.0 (클레이), mix 1.0 + 외곽선, 알베도 ± 외곽선. 정면·측면.
    /// 지표: 대상 비율, 밴드 연결 성분 수(클레이), 비대상 메시 노멀 불변성,
    ///       선 화소 수, 알베도에서 선보다 이미 어두운 화소 비율.
    /// </summary>
    public static class GeneralizationTest
    {
        // Assets/TestSet 의 FBX 를 전부 잡습니다. 새 캐릭터는 파일만 넣으면 됩니다.
        // 이름 -> 에셋 경로. 하위 폴더(예: TestSet/UnityChan/Models/)에 있어도 잡습니다.
        static string PathOf(string c)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(p) == c) return p;
            }
            return "Assets/TestSet/" + c + ".fbx";
        }
        static string[] Chars
        {
            get
            {
                var list = new List<string>();
                foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                        list.Add(Path.GetFileNameWithoutExtension(p));
                }
                list.Sort();
                return list.ToArray();
            }
        }
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        const int Res = 700;
        const float FovY = 24f;

        static readonly StringBuilder Log = new StringBuilder();
        static void L(string m) { Log.AppendLine(m); Console.WriteLine("[gen] " + m); }
        // 외곽선 변형 실험용. FBX2NPR_VARIANT = depthonly(노멀 엣지 끔) / thin(두께 0.5) / silface(얼굴도 실루엣만). 결과는 gen6_<variant>.
        static string Variant { get { var v = Environment.GetEnvironmentVariable("FBX2NPR_VARIANT"); return string.IsNullOrEmpty(v) ? "" : v.Trim().ToLowerInvariant(); } }
        static bool HasTok(string t) { foreach (var x in Variant.Split('+')) if (x == t) return true; return false; }
        static bool SilFace { get { return HasTok("silface"); } }
        // edge: 그림자 밴드를 얼굴 가장자리로 밀어낸다 (밴드 임계값 0.267/0.487 -> 0.0/0.2). 애니메이션 얼굴 관례.
        static bool EdgeBands { get { return HasTok("edge"); } }
        // sub: 서브컬처 얼굴 조명 — 얼굴 조명 노멀을 정면 벡터로 고정(한 톤) + 빛 반대쪽 실루엣만 한 단계 어둡게. 얼굴 메시에만.
        static bool SubFace { get { return HasTok("sub"); } }
        static Vector3 FaceCenterW;   // sub 모드의 띠 방향 기준 (머리 바운드 중심)
        static bool UtsLight;         // uts 모드: 리그에 디렉셔널 라이트를 넣는다 (UTS 는 씬 라이트를 쓴다)
        // wNN 토큰: 가장자리 폭 임계값 NN/100 (기본 0.55). 예: sub+w40
        static float EdgeWidth { get { foreach (var x in Variant.Split('+')) { int n; if (x.Length > 1 && x[0] == 'w' && int.TryParse(x.Substring(1), out n)) return n / 100f; } return 0.95f; } }
        static float Tok(char c, float def) { foreach (var x in Variant.Split('+')) { int n; if (x.Length > 1 && x[0] == c && int.TryParse(x.Substring(1), out n)) return n / 100f; } return def; }
        static float EdgeTaper { get { return Tok('t', 0.15f); } }   // tNN
        static float EdgeDarkAmt { get { return Tok('d', 0.85f); } }  // dNN
        static float EdgeSoft { get { return Tok('s', 0.04f); } }    // sNN
        static float RimLight { get { return Tok('r', 0.40f); } }        // rNN: 빛 쪽 가장자리 밝기
        static float RimWidth { get { return Tok('q', 0.92f); } }    // qNN: 빛 쪽 가장자리 폭
        static bool KeepLit { get { return HasTok("keep"); } }   // keep: 빛 쪽은 원본 노멀 유지, 얼굴 정면화 끔
        // 46번. curve: 얼굴 명암선을 정리된 노멀의 수평 성분 하나로 (한 줄 곡선). nose: 코끝 주변만 원본 노멀 음영.
        static bool CurveFace { get { return HasTok("curve"); } }
        static float CurveStop { get { return Tok('h', 0.25f); } }   // hNN: 명암선 위치 (수평 ndotl 임계값)
        static float CurveTilt { get { return Tok('v', 0f); } }      // vNN: 턱으로 당기기 (0 세로 한 줄, 클수록 턱 끝까지 안쪽으로)
        static float ChinBend { get { return Tok('b', 0.35f); } }    // bNN: 휘는 구간 (작을수록 턱 가까이서 급하게 휨)
        // 50번 기준점 명암선: anchor(켜기), fix(눈꼬리에 고정, 없으면 빛을 따라 움직임),
        // mNN(눈꼬리 바깥 여백, 눈 반폭의 NN%), jNN(턱 끝에서 옆으로, 얼굴 폭의 NN%), aNN(빛 방위각 NN도, 기본 35)
        static bool AnchorOn { get { return HasTok("anchor"); } }
        static bool AnchorFix { get { return HasTok("fix"); } }
        static float CornerMargin { get { return Tok('m', 0.30f); } }
        static float JawOffset { get { return Tok('j', 0.12f); } }
        static float LightAzDeg { get { return Tok('a', 0.35f) * 100f; } }
        static Vector3 RadialCenterW;   // 방사형 노멀 중심 (ApplyMix 와 같은 규칙)
        static Vector3 LightDirFor(float azDeg)
        {
            float e = 25f * Mathf.Deg2Rad, a = azDeg * Mathf.Deg2Rad;
            return (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a)) + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a)) + Vector3.up * Mathf.Sin(e)).normalized;
        }
        static bool NoseOn { get { return HasTok("nose"); } }
        // nNN: 코 음영 임계값 (작을수록 코 그림자가 가늘어진다). nauto: 캐릭터마다 자동 보정한 값 (52번, FaceLandmarks.CalibrateNoseStop)
        static float NoseStop { get { return (HasTok("nauto") && Marks != null && Marks.noseFound) ? Marks.noseStop : Tok('n', 0.30f); } }
        static float NoseStrength { get { return Tok('k', 1.0f); } }  // kNN: 코 음영 진하기 (1 이면 얼굴 그림자와 같은 톤, 작을수록 연함)
        static bool NoseDbg { get { return HasTok("nosedbg"); } }    // 코(빨강)·눈(파랑) 영역 표시
        static bool NoseMoon { get { return HasTok("moon") && HasTok("nauto"); } }   // 54번. 코가 평평한 캐릭터는 반달 도형 (nauto 로 임계값을 올린 경우만)
        // 52번. 코 영역 반경 덮어쓰기: nxNN 좌우(얼굴 폭의 NN%), nuNN 위, ndNN 아래(얼굴 높이의 NN%). 없으면 FaceLandmarks 기본값.
        static float TokS(string p, float def) { foreach (var x in Variant.Split('+')) { int n; if (x.Length > p.Length && x.StartsWith(p) && int.TryParse(x.Substring(p.Length), out n)) return n / 100f; } return def; }

        /// <summary>
        /// 52번 측정: 임계값별로 정면에서 보이는 코 그림자 면적을 남긴다 (FaceLandmarks.NoseShadowArea, 셰이더와 같은 식).
        /// 캐릭터마다 코의 꺾임이 달라(Ch07 은 콧대 옆면이 가파르고 유니티짱은 거의 평평) 임계값 하나로는 모양이 맞지 않는다.
        /// 면적은 눈~코끝 높이의 제곱으로 나눠 캐릭터 크기와 무관하게 비교한다.
        /// </summary>
        static void LogNoseStats(List<FaceLandmarks.Src> srcs)
        {
            if (Marks == null || !Marks.noseFound) return;
            float[] ts = { -0.20f, -0.10f, 0f, 0.05f, 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.35f, 0.40f };
            float sMin;
            var area = Marks.NoseShadowArea(srcs, LightDirFor(LightAzDeg), ts, out sMin);
            float d2 = Marks.EyeNoseDrop() * Marks.EyeNoseDrop();
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "  코 음영 면적(정면에서 보이는 면, 눈~코끝 높이 {0:F3}m 제곱 대비 %, 안쪽 최소 s {1:F2}):", Marks.EyeNoseDrop(), sMin);
            for (int k = 0; k < ts.Length; k++) sb.AppendFormat(CultureInfo.InvariantCulture, " n{0:F2}={1:F2}", ts[k], 100f * area[k] / d2);
            L(sb.ToString());
        }
        static bool EyeAvoid { get { return HasTok("eye"); } }       // 47번. eye: 명암선이 눈을 피해 바깥으로 휘어 돈다
        static FaceLandmarks Marks;                                   // 코끝·눈 기준점 (curve 모드에서만)
        // 63번. flat: 얼굴 피부만 평평한 색으로 굽고(SkinFlatBake) 무가공 사본과 나란히 렌더한다. 기준점이 필요하므로 curve 와 같이 쓴다.
        // fforce: '이미 평평함' 판정 무시, fnosekill: 콧구멍 자리 지우기(기본은 유지), flNN/fhNN: 어두운 획 약/강, fcNN: 색차 약(강 = +6), fgNN: 전역 이탈 약(강 = +15),
        // feNN: 경계 기울기 하한 x10 (fe50 = 5.0), ffNN: 남긴 성분을 단색으로 NN%, frNN: 국소 반경(기준 길이의 NN%), fmNN: '이미 평평함' 문턱(잔무늬 NN/100)
        static bool FlatOn { get { return HasTok("flat"); } }
        static SkinFlatCore.Opt FlatOpt
        {
            get
            {
                var o = new SkinFlatCore.Opt();
                o.tL0 = TokS("fl", o.tL0 / 100f) * 100f; o.tL1 = TokS("fh", o.tL1 / 100f) * 100f;
                o.tC0 = TokS("fc", o.tC0 / 100f) * 100f; o.tC1 = o.tC0 + 6f;
                o.tG0 = TokS("fg", o.tG0 / 100f) * 100f; o.tG1 = o.tG0 + 15f;
                o.minEdge = TokS("fe", o.minEdge / 10f) * 10f; o.featFlat = TokS("ff", o.featFlat);
                o.radiusFrac = TokS("fr", o.radiusFrac); o.gate = TokS("fm", o.gate);
                o.force = HasTok("fforce");
                return o;
            }
        }
        static readonly Color32[][] LastRgb = new Color32[2][];       // 마지막 Shoot 의 색 화소 [정면, 측면]. 색으로 등급을 나눈 마스크 렌더를 읽을 때 쓴다.
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", Variant == "" ? "gen6" : "gen6_" + Variant.Replace('+', '_')); } }

        class Part
        {
            public string name;
            public Mesh mesh;            // 복사본. 노멀을 바꿔 씁니다.
            public Vector3[] origN;      // 원본 노멀
            public Matrix4x4 l2w;
            public float[] weights;      // 대상 가중치 (0~1), 비대상은 0
            public int headCount;        // 가중치 >= 임계 정점 수
            public Texture albedo;
            public bool isFace;          // 재질명·메시명 기준으로 얼굴로 판정된 메시
            public float[] boundsW;      // 카메라 프레이밍용 머리 본 가중치 (얼굴 여부 무관)
        }

        [MenuItem("FBX2NPR/3체 일반화 실험")]
        public static void Run()
        {
            Log.Clear();
            Directory.CreateDirectory(OutDir);
            try
            {
                var feat = FindFeature();
                if (feat == null) { L("외곽선 피처 없음"); Finish(); return; }
                feat.settings.depthScale = 12f; feat.settings.normalScale = 0.8f;
                feat.settings.threshold = 0.30f; feat.settings.thickness = 1.0f;
                feat.settings.darkness = 0.35f; feat.settings.debug = 0f;
                if (HasTok("depthonly")) feat.settings.normalScale = 0f;
                if (HasTok("thin")) feat.settings.thickness = 0.5f;
                if (Variant != "") L("변형: " + Variant + " (normalScale " + feat.settings.normalScale + ", thickness " + feat.settings.thickness + ", 얼굴 실루엣만 " + SilFace + ")");
                ShaderUtil.allowAsyncCompilation = false;

                var summary = new List<string>();
                var only = Environment.GetEnvironmentVariable("FBX2NPR_ONLY");   // "unitychan;Ch07" 처럼 일부만 돌릴 때
                foreach (var c in Chars)
                {
                    if (!string.IsNullOrEmpty(only) && Array.IndexOf(only.Split(';'), c) < 0) { L("건너뜀(FBX2NPR_ONLY): " + c); continue; }
                    L(""); L("==================== " + c);
                    var row = RunOne(c, feat);
                    if (row != null) summary.Add(row);
                }

                L(""); L("==================== 요약");
                L("캐릭터     얼굴%   성분 정면 mix0->1  감소%   성분 측면 mix0->1  감소%   비대상최대각   클레이<140%   알베도 선평균보다 어두움%(측면)");
                foreach (var r in summary) L(r);
            }
            catch (Exception ex) { L("예외: " + ex); }
            Finish();
        }

        static string RunOne(string c, SobelOutlineFeature feat)
        {
            string path = PathOf(c);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { L("  프리팹 없음: " + path); return null; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.hideFlags = HideFlags.HideAndDontSave;
            var parts = new List<Part>();
            int totalV = 0, totalHead = 0, headBoneOnly = 0;
            // 얼굴 판정 규칙: 모델 .meta 의 userData 에 저장된 설정을 우선 쓰고, 없으면 기본값.
            // 재질명 규칙이 모델마다 다르기 때문입니다 (믹사모 "Ch07_Body" 는 피부, 유니티짱 "body" 는 의상).
            var imp = AssetImporter.GetAtPath(path);
            var crit = FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
            L("  얼굴 판정 규칙: 재질 포함=[" + crit.faceMaterialContains + "] 메시 제외=[" + crit.excludeMeshContains + "]" + (imp != null && !string.IsNullOrEmpty(imp.userData) ? " (meta userData)" : " (기본값)"));

            // UV3 점유 확인 (원본 노멀을 심을 자리)
            bool uv3Dup = true, uv3Any = false;

            foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var src = smr.sharedMesh; if (src == null) continue;
                if (crit.IsIgnoredMesh(src.name)) { L("    무시(데칼): " + src.name); continue; }
                var p = new Part();
                p.name = src.name;
                p.mesh = UnityEngine.Object.Instantiate(src);
                p.origN = p.mesh.normals;
                p.l2w = smr.transform.localToWorldMatrix;
                p.albedo = ResolveAlbedo(smr.sharedMaterial, path, crit);

                var uv0 = new List<Vector4>(); var uv3 = new List<Vector4>();
                src.GetUVs(0, uv0); src.GetUVs(3, uv3);
                if (uv3.Count > 0)
                {
                    uv3Any = true;
                    for (int i = 0; i < Mathf.Min(uv0.Count, uv3.Count); i++)
                        if ((uv0[i] - uv3[i]).sqrMagnitude > 1e-10f) { uv3Dup = false; break; }
                }

                // 머리 본 가중치·얼굴 판정 — 변환기·임포터·미리보기와 같은 규칙 (FaceMeshRule). 프레이밍은 머리 본 가중치 전체로, 변환은 얼굴 메시만.
                var fw = FaceMeshRule.Weigh(src, smr.bones, smr.sharedMaterials, crit);
                p.weights = fw.face; p.boundsW = fw.head; p.headCount = fw.faceCount; p.isFace = fw.isFace;
                if (!p.isFace) headBoneOnly += fw.headCount;
                totalV += p.mesh.vertexCount; totalHead += p.headCount;
                parts.Add(p);
            }
            // 스킨이 없는 정적 메시. 유니티짱은 얼굴(MTH_DEF·EYE_DEF·head_back 등)이 전부 머리 본에 붙은 정적 메시다.
            // 본 가중치가 없으므로 조상 트랜스폼 이름에 머리 본 문자열이 있으면 정점 전체를 가중치 1 로 본다.
            foreach (var mf in inst.GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.sharedMesh; if (src == null) continue;
                if (crit.IsIgnoredMesh(src.name)) { L("    무시(데칼): " + src.name); continue; }
                var mr = mf.GetComponent<MeshRenderer>();
                var mfMats = mr != null ? mr.sharedMaterials : null;
                var p = new Part();
                p.name = src.name;
                p.mesh = UnityEngine.Object.Instantiate(src);
                p.origN = p.mesh.normals;
                p.l2w = mf.transform.localToWorldMatrix;
                p.albedo = ResolveAlbedo(mfMats != null && mfMats.Length > 0 ? mfMats[0] : null, path, crit);
                bool underHead = FaceMeshRule.IsUnderHead(mf.transform, inst.transform, crit.headBoneContains);
                var fw = FaceMeshRule.WeighStatic(src, mf.transform, inst.transform, mfMats, crit);
                p.weights = fw.face; p.boundsW = fw.head; p.headCount = fw.faceCount; p.isFace = fw.isFace;
                if (!p.isFace) headBoneOnly += fw.headCount;
                totalV += p.mesh.vertexCount; totalHead += p.headCount;
                L(string.Format("    정적 메시 {0,-14} v={1,5} 머리아래={2} 얼굴={3} 재질={4} 알베도={5}", src.name, src.vertexCount, underHead ? "예" : "아니오", p.isFace ? "예" : "아니오",
                    mfMats == null ? "-" : string.Join("|", Array.ConvertAll(mfMats, m => m == null ? "null" : m.name)), p.albedo == null ? "없음" : p.albedo.name));
                parts.Add(p);
            }
            // 원본 프리팹 인스턴스는 정보만 읽고 렌더링에서 뺍니다.
            // HideAndDontSave 는 렌더링을 끄지 않습니다. 이걸 놓쳐서 1차 실행에서
            // 원본 SMR(URP Lit, 조명 없음)이 클레이 복사본과 같은 자리에서 Z-파이팅했고,
            // 클레이 렌더에 16~47 밝기의 텍스처 화소가 40~60% 섞여 들어갔습니다.
            inst.SetActive(false);

            L(string.Format("  메시 {0}개, 정점 {1}, 대상 {2} ({3:F1}%)", parts.Count, totalV, totalHead, 100.0 * totalHead / Mathf.Max(1, totalV)));
            L("  UV3: " + (uv3Any ? (uv3Dup ? "점유됨, UV0 과 동일 (덮어써도 무해)" : "점유됨, UV0 과 다름 (덮어쓰면 데이터 손실)") : "비어 있음"));
            L(string.Format("  머리 본 가중치만으로 잡혔다가 제외된 정점 {0}개", headBoneOnly));
            foreach (var p in parts)
            {
                int hb = 0; if (p.boundsW != null) foreach (var w in p.boundsW) if (w > 0f) hb++;
                if (hb == 0) continue;
                L(string.Format("    {0,-6} {1,-24} 머리본 {2,6}/{3,-6} ({4:F1}%)",
                    p.isFace ? "얼굴" : "제외", p.name, hb, p.mesh.vertexCount, 100.0 * hb / p.mesh.vertexCount));
            }

            // 원본 노멀을 UV3 에 심음 (선 검출용)
            foreach (var p in parts) p.mesh.SetUVs(3, new List<Vector3>(p.origN));

            // 카메라 프레이밍: 대상 정점의 월드 바운드
            var bb = new Bounds(); bool bbInit = false;
            foreach (var p in parts)
            {
                if (p.boundsW == null) continue;
                var v = p.mesh.vertices;
                for (int i = 0; i < v.Length; i++)
                {
                    if (p.boundsW[i] <= 0f) continue;
                    var w = p.l2w.MultiplyPoint3x4(v[i]);
                    if (!bbInit) { bb = new Bounds(w, Vector3.zero); bbInit = true; } else bb.Encapsulate(w);
                }
            }
            if (!bbInit) { L("  대상 정점 없음"); UnityEngine.Object.DestroyImmediate(inst); return null; }
            L(string.Format("  머리 바운드 중심 {0} 크기 {1}", bb.center, bb.size));
            FaceCenterW = bb.center;

            // 얼굴 기준점(코끝·눈): curve 모드의 코 음영(46번)·눈 피하기(47번) 기준. 규칙은 FaceLandmarks 한곳에 있다 (미리보기 씬과 공용).
            Marks = null;
            {
                // 방사형 노멀 중심: ApplyMix 와 같은 규칙(얼굴 파트 가중치 정점의 중복 제거 평균, FaceMeshRule). 기준점 명암선이 이 중심에서 본 방향으로 임계값을 잰다.
                RadialCenterW = FaceMeshRule.Centroid(Targets(parts));
            }
            if (CurveFace)
            {
                var srcs = new List<FaceLandmarks.Src>();
                foreach (var p in parts)
                {
                    var v = p.mesh.vertices; var wv = new Vector3[v.Length]; var wn = new Vector3[v.Length];
                    var nm = p.l2w.inverse.transpose;   // 셰이더의 TransformObjectToWorldNormal 과 같게
                    for (int i = 0; i < v.Length; i++) { wv[i] = p.l2w.MultiplyPoint3x4(v[i]); wn[i] = nm.MultiplyVector(p.origN[i]).normalized; }
                    srcs.Add(new FaceLandmarks.Src { name = p.name, world = wv, weights = p.weights, isFace = p.isFace, normals = wn, triangles = p.mesh.triangles });
                }
                Marks = FaceLandmarks.Detect(srcs);
                if (Marks.faceFound)
                {
                    var fs = Marks.face.size; var nr = Marks.noseRadius;
                    Marks.noseRadius = new Vector4(TokS("nx", nr.x / fs.x) * fs.x, TokS("nu", nr.y / fs.y) * fs.y, TokS("nd", nr.z / fs.y) * fs.y, nr.w);
                }
                L("  " + Marks.Describe());
                if (NoseOn)
                {
                    LogNoseStats(srcs);
                    Marks.CalibrateNoseStop(srcs, LightDirFor(35f));   // 기준 빛(방위 35도, 고도 25도)에서 보정
                    L("  " + Marks.noseStopNote + (HasTok("nauto") ? " (적용)" : " (nauto 토큰 없음, 미적용)"));
                    // mtNN: 초승달 두께, tri: 세모(56번), tatt: 콧선에 붙이기, thNNN/msNNN: 높이(길이) 배율(밑은 고정, 위로), rquad: 둥근 사다리꼴(57번), rrNN: 모서리 둥글기,
                    // mbNN: 초승달의 가장 두꺼운 곳 위치(59번, 음수면 아래)
                    // mwNNN: 초승달 바깥 폭 배율 (60번). drop: 물방울(61번), drNN: 아래 원 반지름, dhNN: 높이 (눈~코끝 높이의 NN%)
                    int moonShapeTok = HasTok("drop") ? 3 : HasTok("rquad") ? 2 : HasTok("tri") ? 1 : 0;
                    if (NoseMoon) { Marks.FitNoseMoon(srcs, LightDirFor(35f), TokS("mt", FaceLandmarks.MoonThickness), moonShapeTok, HasTok("tatt"), TokS("ms", TokS("th", 1f)), TokS("mb", 0f), TokS("mw", 1f), TokS("dr", FaceLandmarks.DropRadiusFrac), TokS("dh", FaceLandmarks.DropHeightFrac)); L("  " + Marks.moonNote); }
                }
                if (HasTok("statsonly")) { UnityEngine.Object.DestroyImmediate(inst); return null; }   // 측정만 (렌더 생략)
                if (AnchorOn)
                {
                    Marks.ComputeAnchors(srcs, CornerMargin, JawOffset);
                    // 셰이더와 같은 식으로 기준점 임계값을 CPU 에서 계산해 남긴다 (그림자 쪽 = 빛 반대).
                    Vector3 up = Vector3.up, right = Vector3.right;
                    Vector3 ld = LightDirFor(35f); Vector3 lh = (ld - up * Vector3.Dot(ld, up)).normalized;
                    float side = Vector3.Dot(lh, right) >= 0f ? -1f : 1f;
                    Vector3 cp = side > 0f ? Marks.anchorCA : Marks.anchorCB;
                    Vector3 jp = side > 0f ? Marks.anchorJA : Marks.anchorJB;
                    Func<Vector3, float> hs = q => { var d = (q - RadialCenterW).normalized; var h = d - up * Vector3.Dot(d, up); return Vector3.Dot(h, lh) / Mathf.Max(h.magnitude, 0.35f); };
                    L(string.Format(CultureInfo.InvariantCulture, "  기준점(표면, 찾음 {11}): 방사 중심 ({0:F3},{1:F3},{2:F3}) 눈꼬리점 ({3:F3},{4:F3},{5:F3}) s={6:F3} / 턱선점 ({7:F3},{8:F3},{9:F3}) s={10:F3}",
                        RadialCenterW.x, RadialCenterW.y, RadialCenterW.z, cp.x, cp.y, cp.z, hs(cp), jp.x, jp.y, jp.z, hs(jp), Marks.anchorsFound));
                }
            }

            // 클레이도 파트별 머티리얼. 비얼굴 카드는 클레이에서도 알파로 잘라야 속눈썹 모양이 나옵니다.
            var clayMats = new Dictionary<Part, Material>();
            foreach (var p in parts) clayMats[p] = MakeMat(p.albedo, false, SilFace || !p.isFace, !p.isFace, p.isFace);
            var mats = new Dictionary<Part, Material>();

            // 얼굴 마스크: 얼굴 메시만 흰색으로 렌더. 성분 수는 이 안에서만 셉니다.
            // 머리카락·모자를 처리 대상에서 빼면 그쪽 밴드 경계가 그대로 남아,
            // 전체 화면으로 세면 처리 여부와 무관하게 숫자가 나빠집니다.
            var matMask = MakeMat(null, false, false, false);
            matMask.SetVector("_Band0", Vector4.one); matMask.SetVector("_Band1", Vector4.one); matMask.SetVector("_Band2", Vector4.one);
            // 비얼굴 메시는 검정으로 그려 얼굴을 가리게 합니다. 컷아웃을 켜니 머리카락 틈으로 얼굴이
            // 보여서 성분이 잘게 쪼개졌습니다 (Ch07 측면 58→36 이 83→71 로). 가려진 얼굴은 세지 않습니다.
            var maskBlack = new Dictionary<Part, Material>();
            foreach (var p in parts)
            {
                if (p.isFace) continue;
                var mb = MakeMat(p.albedo, false, true, true);
                mb.SetVector("_Band0", Vector4.zero); mb.SetVector("_Band1", Vector4.zero); mb.SetVector("_Band2", Vector4.zero);
                maskBlack[p] = mb;
            }
            feat.SetActive(false); Dirty();
            ApplyMix(parts, 0f);
            var faceMask = Shoot(parts, p => (p.isFace ? matMask : maskBlack[p]), bb, c + "_facemask");
            int maskPx0 = 0, maskPx1 = 0;
            foreach (var v in faceMask[0]) if (v > 128) maskPx0++;
            foreach (var v in faceMask[1]) if (v > 128) maskPx1++;
            L(string.Format("  얼굴 마스크 화소 정면 {0}, 측면 {1}", maskPx0, maskPx1));

            // ---- 조건별 렌더 ----
            var comps = new Dictionary<string, int[]>();   // cond -> [front, side]  (얼굴 마스크 안)
            var compsAll = new Dictionary<string, int[]>();   // 전체 화면 (참고)
            float[] mixes = { 0f, 0.5f, 1f };

            foreach (float mix in mixes)
            {
                ApplyMix(parts, mix);
                feat.SetActive(false); Dirty();
                string key = "mix" + mix.ToString("0.0", CultureInfo.InvariantCulture);
                var r = Shoot(parts, p => clayMats[p], bb, c + "_clay_" + key);
                comps[key] = new[] { CountComponents(r[0], faceMask[0]), CountComponents(r[1], faceMask[1]) };
                compsAll[key] = new[] { CountComponents(r[0], null), CountComponents(r[1], null) };
            }

            // 비대상 불변성: mix 1 적용 후 가중치 0 정점의 노멀 각도차
            ApplyMix(parts, 1f);
            float maxNonTarget = 0f; int nonTargetN = 0;
            foreach (var p in parts)
            {
                var n = p.mesh.normals;
                for (int i = 0; i < n.Length; i++)
                {
                    if (p.weights[i] > 0f) continue;
                    nonTargetN++;
                    // Vector3.Angle 은 acos 기반이라 거의 같은 벡터에서 0.028도 바닥이 생깁니다.
                    // atan2(|a×b|, a·b) 는 0 근처에서 정밀합니다.
                    var a0 = n[i].normalized; var b0 = p.origN[i].normalized;
                    float a = Mathf.Atan2(Vector3.Cross(a0, b0).magnitude, Vector3.Dot(a0, b0)) * Mathf.Rad2Deg;
                    if (a > maxNonTarget) maxNonTarget = a;
                }
            }
            L(string.Format("  비대상 정점 {0}개, 노멀 최대 각도차 {1:F4}도", nonTargetN, maxNonTarget));

            // mix 1 + 외곽선 (클레이)
            feat.SetActive(true); Dirty();
            var rLineClay = Shoot(parts, p => clayMats[p], bb, c + "_clay_mix1.0_line");
            feat.SetActive(false); Dirty();
            var rOffClay = Shoot(parts, p => clayMats[p], bb, c + "_clay_mix1.0_noline");
            var lineClay = LineStats(rOffClay, rLineClay);

            // 알베도 ± 외곽선
            foreach (var p in parts) mats[p] = MakeMat(p.albedo, true, SilFace || !p.isFace, !p.isFace, p.isFace);
            feat.SetActive(false); Dirty();
            var rOffAlb = Shoot(parts, p => mats[p], bb, c + "_albedo_mix1.0_noline");
            feat.SetActive(true); Dirty();
            var rLineAlb = Shoot(parts, p => mats[p], bb, c + "_albedo_mix1.0_line");
            var lineAlb = LineStats(rOffAlb, rLineAlb);
            feat.SetActive(false); Dirty();
            // albmix0: 정리 안 한 노멀(mix 0)에 색을 입힌 칸 (10/19 발표 자료 — 클레이 mix 0 과 같은 조건에 알베도만 켠다)
            if (HasTok("albmix0"))
            {
                ApplyMix(parts, 0f); feat.SetActive(false); Dirty();
                Shoot(parts, p => mats[p], bb, c + "_albedo_mix0.0_noline");
                feat.SetActive(true); Dirty();
                Shoot(parts, p => mats[p], bb, c + "_albedo_mix0.0_line");
                feat.SetActive(false); Dirty();
                ApplyMix(parts, 1f);
            }

            // ---- 63번. 얼굴 피부 평탄화: 텍스처를 굽고, 무가공 사본과 같은 조건으로 렌더한다 (노멀 mix 1, 외곽선 끔/켬).
            // 비교 기준은 원본이 아니라 '무가공 사본'이다. 원본은 압축·임포터 밉이고 구운 것은 무압축·자체 밉이라 그 차이가 섞인다.
            var flatTrash = new List<UnityEngine.Object>();
            if (FlatOn && (Marks == null || !Marks.faceFound || !Marks.noseFound)) L("  평탄화 생략: 기준점 없음 (curve 토큰 필요)");
            else if (FlatOn)
            {
                var fo = FlatOpt;
                var flatTex = new Dictionary<Texture, SkinFlatBake.Result>();
                foreach (var p in parts)
                {
                    if (!p.isFace || p.albedo == null || flatTex.ContainsKey(p.albedo)) continue;
                    // 같은 텍스처를 쓰는 메시는 얼굴이 아니어도 전부 넘긴다(가중치 0). 빼면 그 메시의 UV 섬이 '빈 여백'으로 보여 번짐에 덮인다.
                    var fsrc = new List<SkinFlatBake.Src>();
                    foreach (var q in parts) if (q.albedo == p.albedo) fsrc.Add(new SkinFlatBake.Src { name = q.name, mesh = q.mesh, l2w = q.l2w, weights = q.weights });
                    var r = SkinFlatBake.Bake(p.albedo, fsrc, Marks, fo, HasTok("fnosekill"));
                    flatTex[p.albedo] = r; var k = r.core;
                    if (r.albedo != null) flatTrash.Add(r.albedo); if (r.mask != null) flatTrash.Add(r.mask); if (r.copy != null) flatTrash.Add(r.copy);
                    L(string.Format(CultureInfo.InvariantCulture, "  평탄화 {0} ({1}x{2}, 계산 {3}x{4}): {5} | 잔무늬 {28:F2} (문턱 {29:F2}, 참고: 반경 {31}텍셀로 재면 {32:F2}) 국소 {30:F2} 전체 g={6:F2} 평탄색=({7},{8},{9}) 표본중앙=({10},{11},{12}) 두무리 eta={13:F2} 차={14:F0} | 밀도 {15:F0}텍셀/m 기준길이 {16:F1}텍셀 반경 {17} 번짐 {18} 최소넓이 {19} | 대상 {20} 피부 {21:F1}% 유지 {22:F1}% 성분 {23} | 삼각형 껍질 {24} 그밖 {25} UV밖 {26} {27}",
                        p.albedo.name, r.W, r.H, r.cropW, r.cropH, r.Skipped ? "건너뜀(" + k.note + ")" : "구움", k.gateG, k.flatR, k.flatG, k.flatB, k.medR, k.medG, k.medB, k.otsuEta, k.otsuGap,
                        r.rho, r.hwTex, k.radius, k.grow, k.minArea, k.targetN, k.skinPct, k.keepPct, k.comps, r.shellTris, r.otherTris, r.skippedTris, r.note + k.note, k.gateFine, fo.gate, k.gateLocal, k.gateRadius, k.gateFineScaled));
                    // 건너뛴 경우 예전 실행이 남긴 구운 텍스처를 지운다. 남아 있으면 '건너뜀' 로그와 모순돼 보인다.
                    string bakedPng = Path.Combine(OutDir, c + "_" + p.albedo.name + "_flat_albedo.png");
                    if (r.albedo != null) File.WriteAllBytes(bakedPng, r.albedo.EncodeToPNG()); else if (File.Exists(bakedPng)) File.Delete(bakedPng);
                    if (r.mask != null) File.WriteAllBytes(Path.Combine(OutDir, c + "_" + p.albedo.name + "_flat_mask.png"), r.mask.EncodeToPNG());
                }
                // 재질 세 벌: 사본 / 평탄 / 등급 마스크(빨강 = 평탄화한 피부, 초록 = 남긴 이목구비, 노랑 = 대상이지만 건너뛰어 손대지 않음,
                //   하늘 = 가중치 0~0.5 전이 구간(가중치만큼 섞임), 파랑 = 얼굴 메시지만 대상 아님, 검정 = 비얼굴)
                var copyMats = new Dictionary<Part, Material>(); var flatMats = new Dictionary<Part, Material>(); var fmaskMats = new Dictionary<Part, Material>();
                foreach (var p in parts)
                {
                    SkinFlatBake.Result r = null; if (p.isFace && p.albedo != null) flatTex.TryGetValue(p.albedo, out r);
                    if (!p.isFace) { copyMats[p] = mats[p]; flatMats[p] = mats[p]; fmaskMats[p] = maskBlack[p]; continue; }
                    Material fm;
                    if (r == null)
                    {
                        copyMats[p] = mats[p]; flatMats[p] = mats[p];
                        fm = MakeMat(null, false, false, false);
                        fm.SetVector("_Band0", new Vector4(0, 0, 1, 1)); fm.SetVector("_Band1", new Vector4(0, 0, 1, 1)); fm.SetVector("_Band2", new Vector4(0, 0, 1, 1));
                    }
                    else
                    {
                        // 건너뛴(이미 평평한) 얼굴은 평탄 쪽에도 사본을 써야 두 렌더가 같아진다. 원본을 쓰면 압축 차이로 달라진다.
                        copyMats[p] = MakeMat(r.copy, true, SilFace, false, true); flatMats[p] = MakeMat(r.Skipped ? r.copy : r.albedo, true, SilFace, false, true);
                        flatTrash.Add(copyMats[p]); flatTrash.Add(flatMats[p]);
                        fm = MakeMat(r.mask, true, false, false);
                        fm.SetVector("_Band0", Vector4.one); fm.SetVector("_Band1", Vector4.one); fm.SetVector("_Band2", Vector4.one);
                    }
                    fmaskMats[p] = fm; flatTrash.Add(fm);
                }
                feat.SetActive(false); Dirty();
                var rOffCopy = Shoot(parts, p => copyMats[p], bb, c + "_albedocopy_mix1.0_noline");
                var rOffFlat = Shoot(parts, p => flatMats[p], bb, c + "_albedoflat_mix1.0_noline");
                Shoot(parts, p => fmaskMats[p], bb, c + "_flatmask");
                // 대상(평탄화 대상 얼굴 피부) = 빨강·초록·노랑. 지표는 이 안에서만 잰다 — 얼굴 메시가 몸까지 통짜인 캐릭터(Ch07, Jones) 때문.
                // 대상은 색이 아니라 얼굴 가중치와 메시 껍질로 정해지므로 알고리즘이 스스로 고른 화소가 아니다. 초록 비율만 자기 판정을 다시 읽는 값이다.
                var target = new byte[2][]; var keptPx = new int[2]; var targetPx = new int[2]; var rampPx = new int[2];
                for (int d = 0; d < 2; d++)
                {
                    var px = LastRgb[d]; target[d] = new byte[px.Length];
                    for (int i = 0; i < px.Length; i++)
                    {
                        bool t = px[i].b < 128 && (px[i].r > 128 || px[i].g > 128);
                        target[d][i] = t ? (byte)255 : (byte)0; if (t) { targetPx[d]++; if (px[i].g > 128 && px[i].r < 128) keptPx[d]++; }
                        if (px[i].b > 128 && px[i].g > 64 && px[i].r < 128) rampPx[d]++;
                    }
                }
                feat.SetActive(true); Dirty();
                var rLineCopy = Shoot(parts, p => copyMats[p], bb, c + "_albedocopy_mix1.0_line");
                var rLineFlat = Shoot(parts, p => flatMats[p], bb, c + "_albedoflat_mix1.0_line");
                feat.SetActive(false); Dirty();
                var lsC = LineStats(rOffCopy, rLineCopy, target); var lsF = LineStats(rOffFlat, rLineFlat, target);
                L(string.Format(CultureInfo.InvariantCulture, "  평탄화 대상 화소 정면 {0} (그중 유지로 판정한 화소 {1:F1}%, 전이 구간 {4}), 측면 {2} ({3:F1}%, 전이 구간 {5})",
                    targetPx[0], targetPx[0] > 0 ? 100f * keptPx[0] / targetPx[0] : 0f, targetPx[1], targetPx[1] > 0 ? 100f * keptPx[1] / targetPx[1] : 0f, rampPx[0], rampPx[1]));
                // 이 지표는 '선보다 어두운 화소가 줄었는가'만 잰다. 이목구비까지 지워도 좋아지므로(Jones 가 가장 크게 좋아짐) 성공 판정에 쓰면 안 된다.
                // 보존은 Tools/SkinFlat/metrics.ps1 의 상자 지표(볼·이마 대 눈썹·눈·입)와 눈으로 본다.
                // 선평균은 사본 것으로 고정해서도 잰다. 평탄화가 선 화소 판정(밑색이 어두우면 선으로 안 세어짐)을 바꾸기 때문.
                L(string.Format(CultureInfo.InvariantCulture, "  얼룩 제거량(보존은 재지 않음) 대상 안 알베도 선(사본 -> 평탄): 정면 선평균 {0:F1} -> {1:F1}, 선평균보다 어두움 {2:F2}% -> {3:F2}% (사본 선평균 고정 {4:F2}%) | 측면 {5:F1} -> {6:F1}, {7:F2}% -> {8:F2}% (고정 {9:F2}%)",
                    lsC[0].lineMean, lsF[0].lineMean, lsC[0].darkerPct, lsF[0].darkerPct, DarkerPct(rOffFlat[0], target[0], lsC[0].lineMean),
                    lsC[1].lineMean, lsF[1].lineMean, lsC[1].darkerPct, lsF[1].darkerPct, DarkerPct(rOffFlat[1], target[1], lsC[1].lineMean)));
            }

            // 로그
            L(string.Format("  성분 얼굴만(정면/측면) mix0 {0}/{1}  mix0.5 {2}/{3}  mix1 {4}/{5}",
                comps["mix0.0"][0], comps["mix0.0"][1], comps["mix0.5"][0], comps["mix0.5"][1], comps["mix1.0"][0], comps["mix1.0"][1]));
            L(string.Format("  성분 전체화면(정면/측면) mix0 {0}/{1}  mix0.5 {2}/{3}  mix1 {4}/{5}",
                compsAll["mix0.0"][0], compsAll["mix0.0"][1], compsAll["mix0.5"][0], compsAll["mix0.5"][1], compsAll["mix1.0"][0], compsAll["mix1.0"][1]));
            // 두 가지 어두움 지표:
            //   선평균 기준 — 선이 텍스처와 얼마나 경쟁하는가 (알베도 비교용)
            //   고정 140 기준 — 밴드가 만들 수 없는 어둠 (클레이 대조군은 반드시 0)
            L(string.Format("  클레이 선: 정면 선화소 {0}, 선평균 {1:F1}, 선평균보다 어두움 {2} ({3:F2}%), <140 {4} ({5:F2}%)  | 측면 {6}, {7:F1}, {8} ({9:F2}%), <140 {10} ({11:F2}%)",
                lineClay[0].linePx, lineClay[0].lineMean, lineClay[0].darkerBefore, lineClay[0].darkerPct, lineClay[0].darkFixed, lineClay[0].darkFixedPct,
                lineClay[1].linePx, lineClay[1].lineMean, lineClay[1].darkerBefore, lineClay[1].darkerPct, lineClay[1].darkFixed, lineClay[1].darkFixedPct));
            L(string.Format("  알베도 선: 정면 선화소 {0}, 선평균 {1:F1}, 선평균보다 어두움 {2} ({3:F2}%), <140 {4} ({5:F2}%)  | 측면 {6}, {7:F1}, {8} ({9:F2}%), <140 {10} ({11:F2}%)",
                lineAlb[0].linePx, lineAlb[0].lineMean, lineAlb[0].darkerBefore, lineAlb[0].darkerPct, lineAlb[0].darkFixed, lineAlb[0].darkFixedPct,
                lineAlb[1].linePx, lineAlb[1].lineMean, lineAlb[1].darkerBefore, lineAlb[1].darkerPct, lineAlb[1].darkFixed, lineAlb[1].darkFixedPct));

            float reduce = comps["mix0.0"][0] > 0 ? 100f * (1f - (float)comps["mix1.0"][0] / comps["mix0.0"][0]) : 0f;
            float reduceS = comps["mix0.0"][1] > 0 ? 100f * (1f - (float)comps["mix1.0"][1] / comps["mix0.0"][1]) : 0f;
            string row = string.Format(CultureInfo.InvariantCulture,
                "{0,-9} {1,5:F1}%   {2,4} -> {3,-4} {4,6:F1}%   {5,4} -> {6,-4} {7,6:F1}%   {8,7:F4}도   {9,6:F2}%   {10,6:F2}%",
                c, 100.0 * totalHead / Mathf.Max(1, totalV),
                comps["mix0.0"][0], comps["mix1.0"][0], reduce,
                comps["mix0.0"][1], comps["mix1.0"][1], reduceS,
                maxNonTarget, lineClay[1].darkFixedPct, lineAlb[1].darkerPct);

            // 공식 Unity Toon Shader 로 같은 메시를 렌더: 원본 노멀(mix0) vs 정리된 노멀(mix1).
            // 우리 외곽선은 끄고 UTS 외곽선도 0. 셰이더만 바꾸면 되는가에 대한 대조 실험.
            if (HasTok("uts"))
            {
                feat.SetActive(false); Dirty();
                var prevMode = RenderSettings.ambientMode; var prevAmb = RenderSettings.ambientLight;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = Color.black;
                UtsLight = true;
                var utsClay = new Dictionary<Part, Material>(); var utsTex = new Dictionary<Part, Material>();
                foreach (var p in parts) { utsClay[p] = MakeUtsMat(p.albedo, true, !p.isFace); utsTex[p] = MakeUtsMat(p.albedo, false, !p.isFace); }
                var utsComps = new Dictionary<string, int[]>();
                foreach (var mix in new[] { 0f, 1f })
                {
                    ApplyMix(parts, mix);
                    string key = "mix" + mix.ToString("0.0", CultureInfo.InvariantCulture);
                    var rc = Shoot(parts, p => utsClay[p], bb, c + "_uts_clay_" + key);
                    utsComps[key] = new[] { CountComponents(rc[0], faceMask[0]), CountComponents(rc[1], faceMask[1]) };
                    Shoot(parts, p => utsTex[p], bb, c + "_uts_albedo_" + key);
                }
                UtsLight = false;
                RenderSettings.ambientMode = prevMode; RenderSettings.ambientLight = prevAmb;
                L(string.Format("  [UTS] 성분 얼굴만(정면/측면) mix0 {0}/{1}  mix1 {2}/{3}", utsComps["mix0.0"][0], utsComps["mix0.0"][1], utsComps["mix1.0"][0], utsComps["mix1.0"][1]));
                foreach (var m in utsClay.Values) if (m != null) UnityEngine.Object.DestroyImmediate(m);
                foreach (var m in utsTex.Values) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            }

            foreach (var o in flatTrash) if (o != null) UnityEngine.Object.DestroyImmediate(o);   // 평탄화가 만든 텍스처·재질 (mats·maskBlack 것은 아래에서)
            foreach (var p in parts) UnityEngine.Object.DestroyImmediate(p.mesh);
            foreach (var m in mats.Values) UnityEngine.Object.DestroyImmediate(m);
            foreach (var m in clayMats.Values) UnityEngine.Object.DestroyImmediate(m);
            foreach (var m in maskBlack.Values) UnityEngine.Object.DestroyImmediate(m);
            UnityEngine.Object.DestroyImmediate(matMask);
            UnityEngine.Object.DestroyImmediate(inst);
            return row;
        }

        static void ApplyMix(List<Part> parts, float mix)
        {
            // 중심은 얼굴 파트 전체(월드)에서 한 번만 구한다. 파트별로 구하면 여러 메시로 갈라진 얼굴(유니티짱)에서
            // 메시 경계마다 방사 방향이 달라져 이음새가 생긴다. 변환기·임포터와 같은 규칙 (FaceMeshRule).
            var targets = Targets(parts);
            Vector3 centerW = FaceMeshRule.Centroid(targets);
            for (int k = 0; k < parts.Count; k++)
            {
                var p = parts[k];
                if (p.headCount == 0) { p.mesh.normals = p.origN; continue; }
                p.mesh.normals = FaceMeshRule.Apply(targets[k], centerW, mix);
            }
        }

        static List<FaceMeshRule.Target> Targets(List<Part> parts)
        {
            var list = new List<FaceMeshRule.Target>();
            foreach (var p in parts) list.Add(new FaceMeshRule.Target { vertices = p.mesh.vertices, origN = p.origN, l2w = p.l2w, weights = p.weights, count = p.headCount });
            return list;
        }

        /// <summary>
        /// tex 는 항상 바인딩합니다 (컷아웃이 알파를 읽어야 하므로). useAlbedo 는 RGB 사용 여부.
        /// cutout 은 머리카락·속눈썹 카드용 — 알파가 없는 텍스처면 clip 이 아무것도 안 자릅니다.
        /// </summary>
        static Material MakeMat(Texture tex, bool useAlbedo, bool flatOutline, bool cutout, bool face = false)
        {
            var mat = new Material(Shader.Find("NormalTest/ToonBand"));
            mat.SetVector("_Band0", new Vector4(0.32f, 0.30f, 0.42f, 1f));
            mat.SetVector("_Band1", new Vector4(0.72f, 0.66f, 0.70f, 1f));
            mat.SetVector("_Band2", new Vector4(1.00f, 0.96f, 0.92f, 1f));
            // 58번 실험: 얼굴 피부 그림자 색. 회보라(0.72,0.66,0.70)는 밝은 쪽(1.00,0.96,0.92)보다 파랑이 상대적으로 많이 남아
            // 피부 위에 회색 막을 씌운 것처럼 탁했다. warm = 살구빛, warmp = 분홍빛 (둘 다 리니어 곱셈값, 얼굴 메시에만).
            if (face && HasTok("warm")) mat.SetVector("_Band1", new Vector4(0.86f, 0.62f, 0.60f, 1f));
            if (face && HasTok("warmp")) mat.SetVector("_Band1", new Vector4(0.84f, 0.60f, 0.68f, 1f));
            if (EdgeBands) { mat.SetFloat("_Stop1", 0.0f); mat.SetFloat("_Stop2", 0.2f); } else { mat.SetFloat("_Stop1", 0.267f); mat.SetFloat("_Stop2", 0.487f); }
            mat.SetFloat("_UseOrigNormal", 1f); mat.SetFloat("_NormalStrength", 0f);
            mat.SetFloat("_OutlineFlatNormal", flatOutline ? 1f : 0f);
            if (KeepLit) mat.SetFloat("_OrigBlend", 1f);
            if (SubFace && face) { mat.SetVector("_FaceForward", Vector3.forward); mat.SetVector("_FaceCenter", FaceCenterW); mat.SetFloat("_FaceFlat", KeepLit ? 0f : 1f); mat.SetFloat("_EdgeDark", EdgeDarkAmt); mat.SetFloat("_EdgeWidth", EdgeWidth); mat.SetFloat("_EdgeTaper", EdgeTaper); mat.SetFloat("_EdgeSoft", EdgeSoft); mat.SetFloat("_RimLight", RimLight); mat.SetFloat("_RimWidth", RimWidth); }
            if (CurveFace && face)
            {
                // 46번: 명암선은 정리된 노멀의 수평 성분 하나로. 45번의 원본 섞기·정면화·그림자 띠는 끈다 (림은 sub 설정 그대로).
                mat.SetFloat("_FaceCurve", 1f); mat.SetVector("_FaceUp", Vector3.up); mat.SetVector("_FaceForward", Vector3.forward);
                mat.SetFloat("_CurveStop", CurveStop); mat.SetFloat("_CurveSoft", 0.015f); mat.SetFloat("_CurveTilt", CurveTilt);
                // 코끝에서 턱 끝까지 높이 (턱 끝을 못 찾으면 얼굴 메시 높이의 30%)
                if (Marks != null && Marks.faceFound) mat.SetFloat("_ChinScale", Marks.ChinDrop());
                mat.SetFloat("_ChinBend", ChinBend);
                mat.SetFloat("_FaceFlat", 0f); mat.SetFloat("_EdgeDark", 0f); mat.SetFloat("_OrigBlend", 0f);
                if (Marks != null && Marks.noseFound)
                {
                    mat.SetVector("_NoseTip", Marks.noseTip); mat.SetVector("_NoseRadius", Marks.noseRadius);
                    mat.SetFloat("_NoseShade", NoseOn ? NoseStrength : 0f); mat.SetFloat("_NoseStop", NoseStop); mat.SetFloat("_NoseDebug", NoseDbg ? 1f : 0f);
                    bool moon = NoseOn && NoseMoon && Marks.moonOn;
                    mat.SetFloat("_NoseMoon", moon ? 1f : 0f);
                    if (moon) { mat.SetVector("_NoseMoonShape", Marks.moonShape); mat.SetVector("_NoseMoonParam", Marks.moonParam); mat.SetVector("_NoseMoonExtra", Marks.moonExtra); }
                    if (moon && Marks.moonParam.w > 2.5f)
                    {
                        mat.SetVector("_NosePolyA", Marks.dropA); mat.SetVector("_NosePolyParam", Marks.dropR);   // 61번 물방울
                    }
                    else if (moon && Marks.moonParam.w > 1.5f)
                    {
                        Vector4 sa, sb, sp;
                        ToonFaceRig.RoundQuadParams(Marks.quadA, Marks.quadB, TokS("rr", FaceLandmarks.QuadRound), out sa, out sb, out sp);
                        mat.SetVector("_NosePolyA", sa); mat.SetVector("_NosePolyB", sb); mat.SetVector("_NosePolyParam", sp);
                    }
                }
                if (Marks != null && Marks.eyesFound)
                {
                    mat.SetVector("_EyeA", Marks.eyeA); mat.SetVector("_EyeB", Marks.eyeB); mat.SetVector("_EyeRadius", Marks.eyeRadius);
                    // 추정한 눈(눈 메시 없음)은 자동으로 적용하지 않는다. Jones 에서 비율 추정이 실제 눈보다 한참 위를 잡았다.
                    // 틀린 자리에서 명암선이 휘는 것보다 안 휘는 편이 낫다. 미리보기 씬에서 눈 위치를 옮겨 켤 수 있다.
                    mat.SetFloat("_EyeAvoid", (EyeAvoid && !Marks.eyesEstimated) ? 1f : 0f);
                }
                if (AnchorOn && Marks != null && Marks.anchorsFound)
                {
                    mat.SetFloat("_AnchorOn", 1f); mat.SetFloat("_AnchorFix", AnchorFix ? 1f : 0f);
                    mat.SetVector("_AnchorLight", LightDirFor(35f));   // 기준 빛 = 기본 조명 (방위 35도, 고도 25도)
                    mat.SetVector("_RadialCenter", RadialCenterW);
                    mat.SetVector("_AnchorCA", Marks.anchorCA); mat.SetVector("_AnchorCB", Marks.anchorCB);
                    mat.SetVector("_AnchorJA", Marks.anchorJA); mat.SetVector("_AnchorJB", Marks.anchorJB);
                    mat.SetFloat("_AnchorTopSlope", 0.08f);
                    mat.SetFloat("_AnchorGain", 0.5f * Marks.face.size.x);      // 얼굴 반폭: 방위각 사인이 1 변하면 반폭만큼 이동
                    mat.SetFloat("_AnchorSoft", 0.004f * Marks.face.size.x);    // 700px 렌더에서 1~2 픽셀
                    mat.SetFloat("_EyeAvoid", 0f);
                }
            }
            mat.SetVector("_LightDir", LightDirFor(LightAzDeg));   // aNN 토큰으로 방위각을 바꿀 수 있다 (기본 35도)
            if (tex != null) mat.SetTexture("_Albedo", tex);
            mat.SetFloat("_AlbedoOn", (useAlbedo && tex != null) ? 1f : 0f);
            mat.SetFloat("_Cutout", (cutout && tex != null) ? 1f : 0f);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.SetFloat("_Posterize", 0f); mat.SetFloat("_PosterizeLum", 0f);
            mat.SetFloat("_Flatten", 0f); mat.SetFloat("_Saturation", 1f); mat.SetFloat("_Lift", 0f);
            return mat;
        }

        /// <summary>재질의 메인 텍스처. 없으면 설정의 재질→텍스처 표로 모델 상위 폴더에서 찾는다.</summary>
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
            L("    텍스처 못 찾음: 재질 " + m.name + " -> " + want + " (" + root + ")");
            return null;
        }

        /// <summary>Unity Toon Shader(Toon/Toon) 재질. clay 면 흰 베이스, cutout 이면 베이스맵 알파로 클리핑(그래서 클레이여도 카드 부위는 텍스처를 쓴다).</summary>
        static Material MakeUtsMat(Texture tex, bool clay, bool cutout)
        {
            var sh = Shader.Find("Toon/Toon");
            if (sh == null) { L("  UTS 셰이더 없음 (Toon/Toon)"); return null; }
            var m = new Material(sh);
            bool useTex = tex != null && (!clay || cutout);
            m.SetTexture("_MainTex", useTex ? tex : Texture2D.whiteTexture);
            m.SetColor("_BaseColor", clay && !cutout ? new Color(1.00f, 0.96f, 0.92f) : Color.white);
            m.SetFloat("_Use_BaseAs1st", 1f); m.SetFloat("_Use_1stAs2nd", 1f);
            m.SetColor("_1st_ShadeColor", new Color(0.72f, 0.66f, 0.70f));
            m.SetColor("_2nd_ShadeColor", new Color(0.32f, 0.30f, 0.42f));
            // 우리 측정 임계값(raw ndotl 0.487 / 0.267)을 UTS 의 half-lambert(0.5*x+0.5) 기준으로 환산
            m.SetFloat("_BaseColor_Step", 0.74f); m.SetFloat("_BaseShade_Feather", 0.0001f);
            m.SetFloat("_ShadeColor_Step", 0.63f); m.SetFloat("_1st2nd_Shades_Feather", 0.0001f);
            m.SetFloat("_Is_LightColor_Base", 1f); m.SetFloat("_Set_SystemShadowsToBase", 0f);
            m.SetFloat("_RimLight", 0f); m.SetFloat("_Outline_Width", 0f); m.SetFloat("_GI_Intensity", 0f); m.SetFloat("_Unlit_Intensity", 0f); m.SetFloat("_Is_BLD", 0f);
            if (cutout && tex != null) { m.SetFloat("_ClippingMode", 1f); m.EnableKeyword("_IS_CLIPPING_MODE"); m.SetFloat("_IsBaseMapAlphaAsClippingMask", 1f); m.SetFloat("_Clipping_Level", 0.5f); }
            else { m.SetFloat("_ClippingMode", 0f); m.EnableKeyword("_IS_CLIPPING_OFF"); }
            m.EnableKeyword("_OUTLINE_NML"); m.EnableKeyword("_IS_OUTLINE_CLIPPING_NO");
            return m;
        }

        /// <summary>정면(+Z)·측면(+X) 두 장. 반환은 [정면, 측면] 회색조 배열.</summary>
        static byte[][] Shoot(List<Part> parts, Func<Part, Material> matOf, Bounds bb, string tag)
        {
            var result = new byte[2][];
            var dirs = new[] { Vector3.forward, Vector3.right };
            var names = new[] { "front", "side" };
            for (int d = 0; d < 2; d++)
            {
                var root = new GameObject("GenRig"); root.hideFlags = HideFlags.HideAndDontSave;
                foreach (var p in parts)
                {
                    var mm = matOf(p);
                    if (mm == null) continue;   // 마스크 렌더 등에서 일부 메시만 그릴 때
                    var go = new GameObject(p.name); go.transform.SetParent(root.transform, false);
                    go.transform.position = p.l2w.GetColumn(3);
                    go.transform.rotation = p.l2w.rotation;
                    go.transform.localScale = p.l2w.lossyScale;
                    go.AddComponent<MeshFilter>().sharedMesh = p.mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mm;
                    mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
                }
                float r = Mathf.Max(bb.size.x, Mathf.Max(bb.size.y, bb.size.z)) * 0.5f;
                float dist = r / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * 1.25f;
                var camGo = new GameObject("Cam"); camGo.transform.SetParent(root.transform, false);
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Color.black;
                cam.fieldOfView = FovY; cam.aspect = 1f;
                cam.nearClipPlane = Mathf.Max(0.001f, dist - r * 3f); cam.farClipPlane = dist + r * 8f;
                cam.allowHDR = false; cam.allowMSAA = false;
                var acd = camGo.AddComponent<UniversalAdditionalCameraData>();
                acd.renderPostProcessing = false; acd.antialiasing = AntialiasingMode.None;
                acd.dithering = false; acd.renderShadows = false;
                camGo.transform.position = bb.center + dirs[d] * dist;
                camGo.transform.rotation = Quaternion.LookRotation(bb.center - camGo.transform.position, Vector3.up);
                if (UtsLight)
                {
                    float e = 25f * Mathf.Deg2Rad, a = 35f * Mathf.Deg2Rad;
                    var L0 = (Vector3.forward * (Mathf.Cos(e) * Mathf.Cos(a)) + Vector3.right * (Mathf.Cos(e) * Mathf.Sin(a)) + Vector3.up * Mathf.Sin(e)).normalized;
                    var lg = new GameObject("Sun"); lg.transform.SetParent(root.transform, false);
                    var li = lg.AddComponent<Light>(); li.type = LightType.Directional; li.intensity = 1f; li.color = Color.white; li.shadows = LightShadows.None;
                    lg.transform.rotation = Quaternion.LookRotation(-L0, Vector3.up);   // 빛은 L0 에서 와서 -L0 방향으로 간다
                }

                var rt = new RenderTexture(Res, Res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB); rt.Create();
                bool done = false;
                try { var rq = new UniversalRenderPipeline.SingleCameraRequest(); rq.destination = rt;
                      if (RenderPipeline.SupportsRenderRequest(cam, rq)) { cam.SubmitRenderRequest(rq); done = true; } } catch { }
                if (!done) { cam.targetTexture = rt; cam.Render(); cam.targetTexture = null; }
                var prev = RenderTexture.active; RenderTexture.active = rt;
                var t = new Texture2D(Res, Res, TextureFormat.RGBA32, false, false);
                t.ReadPixels(new Rect(0, 0, Res, Res), 0, 0); t.Apply(); RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(OutDir, tag + "_" + names[d] + ".png"), t.EncodeToPNG());

                var px = t.GetPixels32(); var g = new byte[px.Length];
                for (int i = 0; i < px.Length; i++) g[i] = (byte)((px[i].r + px[i].g + px[i].b) / 3);
                result[d] = g; LastRgb[d] = px;
                UnityEngine.Object.DestroyImmediate(t); rt.Release(); UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(root);
            }
            return result;
        }

        /// <summary>
        /// 클레이 렌더의 밴드 연결 성분 수. 회색값으로 3밴드 분류 후 4연결 성분 (면적 4 이상).
        /// 밴드 색 (sRGB 대략): band0 ~155, band1 ~215, band2 ~250.
        /// </summary>
        static int CountComponents(byte[] g, byte[] mask)
        {
            int n = Res * Res; var label = new int[n];
            for (int i = 0; i < n; i++)
            {
                int v = g[i];
                bool outside = v <= 8 || (mask != null && mask[i] <= 128);
                label[i] = outside ? -1 : (v < 190 ? 0 : (v < 238 ? 1 : 2));
            }
            var seen = new bool[n]; int count = 0; var stack = new Stack<int>();
            for (int s = 0; s < n; s++)
            {
                if (seen[s] || label[s] < 0) continue;
                int lab = label[s], area = 0; stack.Push(s); seen[s] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); area++;
                    int x = i % Res, y = i / Res;
                    if (x > 0 && !seen[i - 1] && label[i - 1] == lab) { seen[i - 1] = true; stack.Push(i - 1); }
                    if (x < Res - 1 && !seen[i + 1] && label[i + 1] == lab) { seen[i + 1] = true; stack.Push(i + 1); }
                    if (y > 0 && !seen[i - Res] && label[i - Res] == lab) { seen[i - Res] = true; stack.Push(i - Res); }
                    if (y < Res - 1 && !seen[i + Res] && label[i + Res] == lab) { seen[i + Res] = true; stack.Push(i + Res); }
                }
                if (area >= 4) count++;
            }
            return count;
        }

        struct LineStat { public int linePx; public float lineMean; public int darkerBefore; public float darkerPct; public int darkFixed; public float darkFixedPct; }

        /// <summary>선 켜기 전/후 비교. 선 화소 = 12 이상 어두워진 전경 화소. 선전 어두운화소 = 선 평균보다 어두웠던 전경 화소.</summary>
        static LineStat[] LineStats(byte[][] off, byte[][] on)
        {
            var res = new LineStat[2];
            for (int d = 0; d < 2; d++)
            {
                var o = off[d]; var l = on[d];
                long sum = 0; int cnt = 0; int fg = 0;
                for (int i = 0; i < o.Length; i++)
                {
                    if (o[i] <= 8) continue; fg++;
                    if (o[i] - l[i] > 12) { sum += l[i]; cnt++; }
                }
                float mean = cnt > 0 ? (float)sum / cnt : 0f;
                int darker = 0, darkFixed = 0;
                for (int i = 0; i < o.Length; i++)
                {
                    if (o[i] <= 8) continue;
                    if (cnt > 0 && o[i] < mean) darker++;
                    if (o[i] < 140) darkFixed++;
                }
                res[d] = new LineStat { linePx = cnt, lineMean = mean, darkerBefore = darker, darkerPct = fg > 0 ? 100f * darker / fg : 0f,
                                        darkFixed = darkFixed, darkFixedPct = fg > 0 ? 100f * darkFixed / fg : 0f };
            }
            return res;
        }

        /// <summary>LineStats 와 같은 정의인데 mask 가 128 초과인 화소만 센다 (평탄화 대상 얼굴 피부 안).</summary>
        static LineStat[] LineStats(byte[][] off, byte[][] on, byte[][] mask)
        {
            var res = new LineStat[2];
            for (int d = 0; d < 2; d++)
            {
                var o = off[d]; var l = on[d]; var mk = mask[d];
                long sum = 0; int cnt = 0; int fg = 0;
                for (int i = 0; i < o.Length; i++)
                {
                    if (o[i] <= 8 || mk[i] <= 128) continue; fg++;
                    if (o[i] - l[i] > 12) { sum += l[i]; cnt++; }
                }
                float mean = cnt > 0 ? (float)sum / cnt : 0f;
                int darker = 0, darkFixed = 0;
                for (int i = 0; i < o.Length; i++)
                {
                    if (o[i] <= 8 || mk[i] <= 128) continue;
                    if (cnt > 0 && o[i] < mean) darker++;
                    if (o[i] < 140) darkFixed++;
                }
                res[d] = new LineStat { linePx = cnt, lineMean = mean, darkerBefore = darker, darkerPct = fg > 0 ? 100f * darker / fg : 0f,
                                        darkFixed = darkFixed, darkFixedPct = fg > 0 ? 100f * darkFixed / fg : 0f };
            }
            return res;
        }
        /// <summary>mask 안 전경 화소 중 회색값이 mean 보다 작은 비율(%). 선평균을 다른 렌더 것으로 고정해 비교할 때 쓴다.</summary>
        static float DarkerPct(byte[] off, byte[] mask, float mean)
        {
            int fg = 0, darker = 0;
            for (int i = 0; i < off.Length; i++) { if (off[i] <= 8 || mask[i] <= 128) continue; fg++; if (off[i] < mean) darker++; }
            return fg > 0 ? 100f * darker / fg : 0f;
        }

        static SobelOutlineFeature FindFeature()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd == null) return null;
            foreach (var f in rd.rendererFeatures) { var s = f as SobelOutlineFeature; if (s != null) return s; }
            return null;
        }
        static void Dirty()
        {
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath); if (rd == null) return;
            var m = rd.GetType().GetMethod("SetDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (m != null) m.Invoke(rd, null);
        }
        static void Finish()
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, "log_gen.txt"), Log.ToString(), new UTF8Encoding(true));
        }
    }
}
