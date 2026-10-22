// FBX2NPRPainterly.cs — 2-Track 의 둘째 경로: 회화풍(Painterly) 모드 변환 (2026-10-10, 기록 Q 절).
//   텍스처: 모델이 쓰는 알베도마다 PainterlyCore(이방성 쿠와하라 + 흐름 줄무늬 + 마무리) → <이름>_paint.png
//   재질:   FBX2NPR/PainterlyLit (= URP Lit, 외곽선용 노멀 패스만 다름 — 기록 98번) — 붓질 알베도, 노멀맵 없음, 광택 0.15, 알파 있는 텍스처는 컷아웃·양면
//   눈매 잉크를 얹었으면 속눈썹 카드 메시(painterlyHideMeshContains)를 숨긴다 (툰과 같은 규칙, 기록 98번)
//   외곽선: PainterlyRig 의 inkLine 슬라이더가 화면공간 외곽선을 '실루엣만, 얇게'로 쓴다
//   메시·노멀은 건드리지 않는다(원본 FBX 메시를 그대로 참조). 파싱·평탄화·얼굴 리그도 없다.
// 출력: Assets/FBX2NPRConverted/<모델>/<모델>_Painterly.prefab (+ Materials/*_paint.mat, Textures/*_paint.png). 다시 변환하면 내용만 바꾼다(GUID 유지).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRPainterly
    {
        // 눈 사이 거리 추정 = 머리 폭 × 이 값 (툰 경로의 기준점 대체 — 눈 기준점이 없어도 되게). 머리 바운드에는 모자가 들어가 The Boss 가 넓게 잡히므로
        // 0.34 가 아니라 0.29: 그래야 The Boss 가 사용자가 고른 붓 반경 9(1024 텍스처)가 된다.
        public const float BrushIpdFrac = 0.29f;
        /// <summary>회화풍 재질 셰이더 (URP Lit + 외곽선용 노멀 패스의 '멀어지면 안쪽 선 지우기', 기록 98번). 재질이 참조하므로 빌드에 들어간다.</summary>
        public const string PainterlyLitPath = FBX2NPRConverter.ShaderDir + "/PainterlyLit.shader";

        public static FBX2NPRConverter.Report Convert(string modelPath, GameObject prefab, FBX2NPRAssetSettings crit, FBX2NPRConverter.Report rep)
        {
            string c = Path.GetFileNameWithoutExtension(modelPath);
            string dir = FBX2NPRConverter.OutRoot + "/" + FBX2NPRConverter.Safe(c);
            string prefabPath = dir + "/" + FBX2NPRConverter.Safe(c) + "_Painterly.prefab";
            string modelGuid = AssetDatabase.AssetPathToGUID(modelPath);
            var prevGenerated = new List<string>(); AnimatorCullingMode? keepCulling = null;
            {
                var oldGo = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var oldRig = oldGo != null ? oldGo.GetComponentInChildren<PainterlyRig>(true) : null;
                if (oldRig != null && oldRig.generatedAssets != null) prevGenerated.AddRange(oldRig.generatedAssets);
                var oldAn = oldGo != null ? oldGo.GetComponent<Animator>() : null;
                if (oldAn != null) keepCulling = oldAn.cullingMode;   // 다시 변환해도 프리팹의 Animator 컬링 설정은 이어받는다 (FBX2NPRHumanoidSetup 이 AlwaysAnimate 로 둔 것이 모델 기본값으로 돌아갔다, 기록 98번)
            }
            FBX2NPRConverter.EnsureFolder(dir + "/Materials"); FBX2NPRConverter.EnsureFolder(dir + "/Textures");
            bool existed = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
            rep.lines.Add("회화풍 모드 (style = 1): 붓질 알베도 + 표준 Lit + 실루엣 잉크. 메시·노멀은 원본 그대로.");

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            PrefabUtility.UnpackPrefabInstance(inst, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            inst.name = c + "_Painterly";
            var temps = new List<UnityEngine.Object>();
            try
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var texNames = new Dictionary<string, Texture>(StringComparer.OrdinalIgnoreCase);
                // ---- 기준 길이: 머리 폭에서 눈 사이 거리를 어림하고, 텍스처마다 텍셀 밀도를 곱한다
                Bounds head; int hv; bool haveHead = FBX2NPRParseBake.HeadBounds(inst, crit, out head, out hv);
                var bodyB = new Bounds(); bool bInit = false;
                foreach (var r in inst.GetComponentsInChildren<Renderer>()) { if (!bInit) { bodyB = r.bounds; bInit = true; } else bodyB.Encapsulate(r.bounds); }
                float ipd = haveHead ? BrushIpdFrac * head.size.x : 0.06f;
                rep.head = haveHead ? head : bodyB; rep.body = bodyB; rep.faceFound = haveHead;
                rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "기준 길이: 머리 폭 {0:F3}m → 눈 사이 거리 어림 {1:F3}m (툰 경로의 기준점 대신 머리 바운드 × {2:F2})", haveHead ? head.size.x : 0f, ipd, BrushIpdFrac));

                // ---- 렌더러·재질 칸 수집 (데칼 메시는 끈다). 또렷하게 둘 부분(안구 등)도 여기서 표시한다.
                var headBone = FBX2NPRConverter.FindHeadBone(inst.transform, crit.headBoneContains);
                var slots = new List<Slot>(); var rhoTex = new Dictionary<Texture, double[]>();   // 텍스처 → [sumTex, sumWorld]
                var sharpNames = new List<string>(); int sharpComps = 0;
                foreach (var rend in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if (crit.IsIgnoredMesh(rend.name)) { rend.enabled = false; rep.lines.Add("끈 메시(데칼): " + rend.name); continue; }
                    Mesh mesh = null; var smr = rend as SkinnedMeshRenderer;
                    if (smr != null) { if (smr.sharedMesh == null) continue; mesh = new Mesh(); smr.BakeMesh(mesh, true); temps.Add(mesh); }
                    else { var mf = rend.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue; mesh = mf.sharedMesh; }
                    var mats = rend.sharedMaterials; var l2w = rend.transform.localToWorldMatrix;
                    bool sharpAll = crit.IsPainterlySharp(rend.name); if (sharpAll) sharpNames.Add(rend.name);
                    // 이름이 없어도: 머리 본에 묶인 메시의 작은 연결 성분(안구·이·입 안)은 또렷하게 둔다 (툰 경로의 '껍질' 규칙과 같은 발상)
                    bool[] triSharp = null;
                    if (!sharpAll && smr != null && headBone != null) { int n; triSharp = SmallHeadComponents(smr, mesh, headBone, l2w, out n); if (triSharp != null) sharpComps += n; }
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var m = mats.Length > 0 ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                        var alb = FBX2NPRConverter.ResolveAlbedo(m, modelPath, crit);
                        slots.Add(new Slot { rend = rend, slot = s, mat = m, albedo = alb, mesh = mesh, sharpAll = sharpAll, triSharp = triSharp });
                        if (alb != null) Accumulate(rhoTex, alb, mesh, s, l2w);
                    }
                }
                if (sharpNames.Count > 0 || sharpComps > 0)
                    rep.lines.Add("또렷하게 둘 것: 이름으로 " + sharpNames.Count + "개 메시 (" + string.Join(", ", sharpNames.ToArray()) + ")" + (sharpComps > 0 ? ", 얼굴에 붙은 작은 덩어리 " + sharpComps + "개" : ""));

                // ---- 얼굴 피부 마스크: 툰 경로와 같은 얼굴 파싱(7뷰 → UV 라벨). 피부·코·목·귀 라벨 = 붓털 줄무늬 끄고 부드럽게. 파서를 못 믿는 얼굴(Jones)은 라벨이 없어 그대로 붓질.
                var labelsOf = new Dictionary<Texture, FBX2NPRParseBake.TexResult>();
                if (crit.useFaceParser && haveHead)
                {
                    var plog = new List<string>();
                    using (var parser = FaceParser.Load(plog))
                    {
                        if (parser == null) foreach (var l in plog) rep.lines.Add("얼굴 파싱 없이 진행(피부 스무딩 없음): " + l);
                        else
                        {
                            var pgo = (GameObject)PrefabUtility.InstantiatePrefab(prefab); var ptemps = new List<UnityEngine.Object>();
                            try
                            {
                                OriginalMaterialFix.Apply(pgo, crit, modelPath, ptemps);
                                var pr = FBX2NPRParseBake.Bake(pgo, crit, modelPath, parser, 1024, null, null, FBX2NPRConverter.Safe(c));
                                foreach (var t in pr.textures) labelsOf[t.albedo] = t;
                                rep.lines.Add("얼굴 파싱: 텍스처 " + pr.textures.Count + "개에 라벨 (정렬 불일치 " + (pr.alignMismatch >= 0 ? (100f * pr.alignMismatch).ToString("F1", CultureInfo.InvariantCulture) + "%" : "-") + ")");
                            }
                            catch (Exception ex) { rep.lines.Add("얼굴 파싱 실패(피부 스무딩 없음): " + ex.Message); labelsOf.Clear(); }
                            finally { UnityEngine.Object.DestroyImmediate(pgo); foreach (var t in ptemps) if (t != null) UnityEngine.Object.DestroyImmediate(t); }
                        }
                    }
                }

                // ---- 텍스처: 알베도마다 한 번
                var paintOf = new Dictionary<Texture, Texture2D>(); var inkTex = new HashSet<Texture>(); var inkEntries = new List<PainterlyRig.InkEntry>();
                bool eyeInkDrawn = false;   // 어느 텍스처에서든 눈매 잉크(원본 아이라인)를 얹었는가 → 속눈썹 카드 메시를 숨길지 (툰의 lashDrawn 과 같은 규칙)
                foreach (var s in slots)
                {
                    if (s.albedo == null || paintOf.ContainsKey(s.albedo)) continue;
                    double[] acc; float rho = rhoTex.TryGetValue(s.albedo, out acc) && acc[1] > 1e-10 ? (float)Math.Sqrt(acc[0] / acc[1]) : 0f;
                    float hwTex = Mathf.Max(2f, 0.2f * ipd * rho);
                    int W = s.albedo.width, H = s.albedo.height;
                    var px = RegionFlatBake.Read(s.albedo, W, H);
                    var rgba = new byte[W * H * 4];
                    for (int i = 0; i < W * H; i++) { rgba[i * 4] = px[i].r; rgba[i * 4 + 1] = px[i].g; rgba[i * 4 + 2] = px[i].b; rgba[i * 4 + 3] = px[i].a; }
                    // 또렷하게 둘 텍셀 (안구 등): 해당 삼각형을 UV 에 그린다. 이 텍스처가 그런 메시에만 쓰이면 붓질 자체를 건너뛴다.
                    var protect = new float[W * H]; bool anyPaint = false; int protectN = 0;
                    foreach (var q in slots)
                    {
                        if (q.albedo != s.albedo) continue;
                        if (q.sharpAll) RasterTris(protect, W, H, q.mesh, q.slot, null);
                        else { if (q.triSharp != null) RasterTris(protect, W, H, q.mesh, q.slot, q.triSharp); anyPaint = true; }
                    }
                    for (int i = 0; i < W * H; i++) if (protect[i] > 0f) protectN++;
                    if (!anyPaint)
                    {
                        paintOf[s.albedo] = null;   // null = 원본 그대로
                        rep.lines.Add("텍스처 " + s.albedo.name + ": 또렷하게 둘 메시만 쓰므로 붓질하지 않음 (원본 그대로)");
                        continue;
                    }
                    if (protectN > 0) protect = PainterlyCore.BlurMask(protect, W, H, 2.0); else protect = null;
                    // 얼굴 피부 마스크 (라벨 → 텍셀, 가장자리는 넓게 흐림)
                    float[] smooth = null; int smoothN = 0; FBX2NPRParseBake.TexResult tr;
                    if (labelsOf.TryGetValue(s.albedo, out tr))
                    {
                        smooth = new float[W * H];
                        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                        {
                            byte L = tr.label[((2 * y + 1) * tr.H / (2 * H)) * tr.W + ((2 * x + 1) * tr.W / (2 * W))];
                            if (L == FaceParser.Skin || L == FaceParser.Nose || L == FaceParser.Neck || L == FaceParser.LEar || L == FaceParser.REar) { smooth[y * W + x] = 1f; smoothN++; }
                        }
                        if (smoothN > 0 && crit.painterlySkinSmooth > 0f)
                        {
                            smooth = PainterlyCore.BlurMask(smooth, W, H, Math.Max(2.0, 0.6 * hwTex));
                            if (crit.painterlySkinSmooth < 1f) for (int i = 0; i < W * H; i++) smooth[i] *= crit.painterlySkinSmooth;
                        }
                        else smooth = null;
                    }
                    var o = new PainterlyCore.Opt();
                    var orig = (byte[])rgba.Clone();
                    var pr = PainterlyCore.Run(rgba, W, H, hwTex, o, smooth, protect);
                    // 눈가의 어두운 선(아이라인 날개·속눈썹·눈썹 진한 선)을 원본에서 Darken(min)으로 덧그린다 — 사용자 지시(2026-10-10): 마스크로 오려 붙이면 계단이 생기므로
                    // 피부보다 충분히 어두운 화소만, 부드러운 비중으로. 눈 라벨 둘레 2×기준길이, 눈썹 라벨 둘레 0.3×기준길이.
                    int inkN = 0; bool inkStore = false;   // 비중을 텍스처 알파에 구워 런타임에 섞는다 (PainterlyRig.eyeInk 슬라이더). 알파를 쓰는(컷아웃) 텍스처는 예전처럼 굽는다
                    if (tr != null && (crit.painterlyEyeInk > 0f || !FBX2NPRConverter.HasAlpha(s.albedo)))
                    {
                        var zone = new float[W * H]; var browZ = new float[W * H]; bool anyZ = false;
                        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
                        {
                            byte L = tr.label[((2 * y + 1) * tr.H / (2 * H)) * tr.W + ((2 * x + 1) * tr.W / (2 * W))];
                            if (L == FaceParser.LEye || L == FaceParser.REye) { zone[y * W + x] = 1f; anyZ = true; }
                            else if (L == FaceParser.LBrow || L == FaceParser.RBrow) { browZ[y * W + x] = 1f; anyZ = true; }
                        }
                        if (anyZ)
                        {
                            inkStore = !FBX2NPRConverter.HasAlpha(s.albedo); if (inkStore) for (int i = 0; i < W * H; i++) rgba[i * 4 + 3] = 0;
                            zone = Dilate(zone, W, H, Mathf.Max(2, Mathf.RoundToInt(2.0f * hwTex))); browZ = Dilate(browZ, W, H, Mathf.Max(1, Mathf.RoundToInt(0.3f * hwTex)));
                            for (int i = 0; i < W * H; i++) zone[i] = Math.Max(zone[i], browZ[i]);
                            zone = PainterlyCore.BlurMask(zone, W, H, Math.Max(1.5, 0.3 * hwTex));
                            const float t0 = 30f, t1 = 70f;   // 피부(붓질 결과)보다 이만큼 어두운 화소부터 얹기 시작해 t1 에서 완전히 (sRGB 8비트 밝기 차)
                            for (int i = 0; i < W * H; i++)
                            {
                                float z = zone[i]; if (z <= 0.001f) continue;
                                float lb = 0.2126f * rgba[i * 4] + 0.7152f * rgba[i * 4 + 1] + 0.0722f * rgba[i * 4 + 2];
                                float lo = 0.2126f * orig[i * 4] + 0.7152f * orig[i * 4 + 1] + 0.0722f * orig[i * 4 + 2];
                                float d = lb - lo; if (d <= t0) continue;
                                float tt = Mathf.Clamp01((d - t0) / (t1 - t0)); tt = tt * tt * (3f - 2f * tt);
                                float w = z * tt;
                                if (inkStore) { rgba[i * 4 + 3] = (byte)Mathf.RoundToInt(w * 255f); if (w > 0.5f) inkN++; continue; }
                                w *= crit.painterlyEyeInk; if (w > 0.5f) inkN++;
                                for (int k = 0; k < 3; k++) { int b = rgba[i * 4 + k], a = Math.Min(b, (int)orig[i * 4 + k]); rgba[i * 4 + k] = (byte)Mathf.RoundToInt(b + (a - b) * w); }
                            }
                        }
                    }
                    int decalN = 0, decalTex = 0;
                    if (tr != null && crit.painterlyDecalKeep > 0f) decalN = RestoreDecals(rgba, orig, W, H, tr, hwTex, crit.painterlyDecalKeep, out decalTex);
                    var outPx = new Color32[W * H]; for (int i = 0; i < W * H; i++) outPx[i] = new Color32(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2], rgba[i * 4 + 3]);
                    var t = new Texture2D(W, H, TextureFormat.RGBA32, false, false); t.SetPixels32(outPx); t.Apply(false, false);
                    if (decalN > 0) rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "텍스처 {0}: 피부 위 장식 {1}개 되살림 ({2}텍셀, 세기 {3:F2})", s.albedo.name, decalN, decalTex, crit.painterlyDecalKeep));
                    string texBase = TexBase(dir, s.albedo, texNames);
                    var saved = FBX2NPRConverter.SaveTexture(t.EncodeToPNG(), texBase + "_paint.png", s.albedo);
                    UnityEngine.Object.DestroyImmediate(t);
                    used.Add(texBase + "_paint.png"); paintOf[s.albedo] = saved; if (inkStore && inkN > 0) inkTex.Add(s.albedo);
                    if (inkN > 0) eyeInkDrawn = true;
                    rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "텍스처 {0} ({1}x{2}): 붓질 반경 {3}텍셀 (기준 길이 {4:F1}, 밀도 {5:F0}텍셀/m){6}, 줄무늬 길이 {7}·폭 {8}, 피부 스무딩 {9:F1}%, 또렷하게 {10:F1}%, 눈가 어두운 선 {11}텍셀, {12:F1}초 → {13}",
                        s.albedo.name, W, H, pr.radius, hwTex, rho, pr.halfRes ? ", 절반 해상도에서 붓질" : "", pr.streakLen, pr.streakWidth, 100.0 * smoothN / (W * H), 100.0 * protectN / (W * H), inkN, pr.seconds, Path.GetFileName(texBase + "_paint.png")));
                }

                // ---- 재질: FBX2NPR/PainterlyLit (= URP Lit, 외곽선용 노멀 패스만 '멀어지면 안쪽 선 지우기'). 없으면 표준 Lit
                var lit = AssetDatabase.LoadAssetAtPath<Shader>(PainterlyLitPath);
                if (lit == null) lit = Shader.Find(PainterlyRig.FadeShaderName);
                if (lit == null || !lit.isSupported)
                {
                    rep.lines.Add("셰이더 " + PainterlyRig.FadeShaderName + (lit == null ? " 없음" : " 이 이 플랫폼에서 지원되지 않음") + ": 표준 Lit 으로 (툰과 한 씬일 때 먼 거리 안쪽 선 지우기 없음)");
                    lit = Shader.Find("Universal Render Pipeline/Lit");
                }
                if (lit == null) { rep.lines.Add("셰이더 Universal Render Pipeline/Lit 없음"); return rep; }
                var matOf = new Dictionary<Material, Material>(); int matN = 0, cutN = 0;
                foreach (var rend in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if (!rend.enabled) continue;
                    var mats = rend.sharedMaterials; bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var src = mats[i]; if (src == null) continue;
                        Material m;
                        if (!matOf.TryGetValue(src, out m))
                        {
                            var alb = FBX2NPRConverter.ResolveAlbedo(src, modelPath, crit);
                            Texture2D paint; Texture baseTex = alb != null && paintOf.TryGetValue(alb, out paint) && paint != null ? paint : alb;
                            m = new Material(lit);
                            // 참고(기록 98번): URP Lit 재질은 disabledShaderPasses 에 MOTIONVECTORS 가 들어가지만 이 셰이더 재질은 비어 있다. 여기서 꺼도 URP 의 LitShader 인스펙터 검증이
                            // URP 셰이더가 아니면 다시 켠다(BaseShaderGUI.UpdateMotionVectorKeywordsAndPass). 스킨 메시는 이 설정과 상관없이 움직임 벡터 패스를 쓰고(URP 주석), 이 렌더러는 움직임 벡터를 그리지 않아 화면 차이는 없다.
                            m.SetTexture("_BaseMap", baseTex); m.SetColor("_BaseColor", Color.white);
                            m.SetFloat("_Smoothness", 0.15f); m.SetFloat("_Metallic", 0f);
                            m.SetFloat("_SpecularHighlights", 0f); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                            m.SetFloat("_EnvironmentReflections", 0f); m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
                            // 발광 맵은 옮긴다 (보석·불빛) — 툰 변환기와 같은 규칙. 빠뜨려서 Jones 가슴 보석이 검은 원으로 나왔다 (기록 96번)
                            if (src.HasProperty("_EmissionMap") && src.IsKeywordEnabled("_EMISSION"))
                            {
                                var em = src.GetTexture("_EmissionMap"); var ec = src.HasProperty("_EmissionColor") ? src.GetColor("_EmissionColor") : Color.white;
                                if (em != null && ec.maxColorComponent > 0f)
                                {
                                    m.SetTexture("_EmissionMap", em); m.SetColor("_EmissionColor", ec); m.EnableKeyword("_EMISSION");
                                    // GI 플래그가 None 이면 URP 가 저장·검증 때 '발광 없음'으로 보고 _EMISSION 을 꺼 버렸다(1차). 원본 플래그를 따르고, 없으면 실시간 발광으로
                                    var gi = src.globalIlluminationFlags & ~MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                                    m.globalIlluminationFlags = gi == MaterialGlobalIlluminationFlags.None ? MaterialGlobalIlluminationFlags.RealtimeEmissive : gi;
                                    rep.lines.Add("재질 " + src.name + ": 발광 맵 " + em.name + " 을 옮김");
                                }
                            }
                            if (alb != null && FBX2NPRConverter.HasAlpha(alb))
                            {
                                m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.5f); m.EnableKeyword("_ALPHATEST_ON"); m.SetFloat("_Cull", 0f); cutN++;
                                m.renderQueue = 2450;
                            }
                            string matPath = FBX2NPRConverter.UniquePath(dir + "/Materials/" + FBX2NPRConverter.Safe(src.name) + "_paint", ".mat", used);
                            bool wantEm = m.IsKeywordEnabled("_EMISSION"); var giWant = m.globalIlluminationFlags;
                            m = FBX2NPRConverter.SaveOrReplace(m, matPath); matOf[src] = m; matN++;
                            if (wantEm && !m.IsKeywordEnabled("_EMISSION")) { m.globalIlluminationFlags = giWant; m.EnableKeyword("_EMISSION"); EditorUtility.SetDirty(m); rep.lines.Add("  (_EMISSION 키워드를 저장 뒤 다시 켬)"); }
                            if (alb != null && inkTex.Contains(alb)) { Texture2D pt; if (paintOf.TryGetValue(alb, out pt)) inkEntries.Add(new PainterlyRig.InkEntry { material = m, painted = pt, original = alb as Texture2D }); }
                        }
                        mats[i] = m; changed = true;
                    }
                    if (changed) rend.sharedMaterials = mats;
                }
                rep.lines.Add("재질 " + matN + "개 (" + lit.name + ", 노멀맵 없음, 광택 0.15, 컷아웃·양면 " + cutN + "개), 텍스처 " + paintOf.Count + "개");

                // ---- 속눈썹 카드 메시: 눈매 잉크를 얹었으면 숨긴다 (툰 모드와 같은 처리, 기록 98번). 켜 두면 아래 눈꺼풀 밑에 1픽셀 검은 점이 프레임마다 깜빡였다(숨기니 0개).
                //      재질을 바꾼 뒤에 끈다 — 아티스트가 다시 켜면 회화풍 재질로 나온다.
                if (eyeInkDrawn && !string.IsNullOrEmpty(crit.painterlyHideMeshContains))
                {
                    foreach (var rr in inst.GetComponentsInChildren<Renderer>(true))
                    {
                        var smr = rr as SkinnedMeshRenderer; var mf = rr.GetComponent<MeshFilter>();
                        string meshName = smr != null && smr.sharedMesh != null ? smr.sharedMesh.name : (mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : "");
                        if (!rr.enabled || !(crit.IsPainterlyHiddenMesh(rr.name) || crit.IsPainterlyHiddenMesh(meshName))) continue;
                        rr.enabled = false; rep.lines.Add("  숨김: " + rr.name + " (속눈썹 카드 메시 — 눈매 잉크를 얹었으므로, 설정 painterlyHideMeshContains)");
                    }
                }

                // ---- 리그: 잉크 슬라이더 + 만든 에셋 목록
                var rigGo = new GameObject("회화풍 리그"); rigGo.transform.SetParent(inst.transform, false);
                var rig = rigGo.AddComponent<PainterlyRig>();
                rig.sourceModelGuid = modelGuid; rig.inkLine = crit.painterlyInk;
                rig.eyeInk = crit.painterlyEyeInk; rig.inkEntries = inkEntries; rig.inkBlendShader = AssetDatabase.LoadAssetAtPath<Shader>(FBX2NPRConverter.ShaderDir + "/InkBlend.shader");
                if (rig.inkBlendShader == null) rep.lines.Add("InkBlend.shader 없음: 눈매 잉크 슬라이더가 동작하지 않습니다."); else rep.lines.Add("눈매 잉크: 런타임 섞기 " + inkEntries.Count + "개 재질 (슬라이더 초기값 " + crit.painterlyEyeInk.ToString("F2", CultureInfo.InvariantCulture) + ")");
                rig.generatedAssets = new List<string>();
                foreach (var ap in used) { string g = AssetDatabase.AssetPathToGUID(ap); if (!string.IsNullOrEmpty(g)) rig.generatedAssets.Add(g); }
                var feat = FBX2NPRConverter.FindFeature();
                if (feat != null) rig.outlineFeature = feat; else rep.lines.Add("외곽선 피처를 찾지 못함: 잉크 라인 없이 저장합니다.");
                rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "잉크 라인: 세기 {0:F2}, 두께 {1:F1}px, 진하기 {2:F2}, 실루엣(깊이)만", rig.inkLine, rig.inkThickness, rig.inkDarkness));
                // 툰과 한 씬일 때(외곽선이 노멀 엣지를 쓸 때) 멀어지면 안쪽 선 지우기 — 툰과 같은 380→280픽셀 기준, 높이는 머리 본 정점 바운드(회화풍 경로에는 얼굴 기준점이 없다)
                if (haveHead)
                {
                    float fh = Mathf.Max(head.size.y, 0.05f);
                    rig.innerLineFade = new Vector2(fh / FBX2NPRConverter.InnerLineFadeStartPx, fh / FBX2NPRConverter.InnerLineFadeEndPx);
                    rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "멀어지면 안쪽 선 지우기 (툰과 한 씬일 때만): 머리 높이 {0:F3}m 가 화면에서 {1:F0}픽셀보다 작아지면 시작해 {2:F0}픽셀에서 다 지움 (캐릭터 전체)", fh, FBX2NPRConverter.InnerLineFadeStartPx, FBX2NPRConverter.InnerLineFadeEndPx));
                }
                else rep.lines.Add("멀어지면 안쪽 선 지우기: 머리를 못 찾아 끔");

                // 모델에 Animator 는 있는데 아바타가 비어 있으면(Generic + 아바타 없음으로 임포트했던 경우) 모델의 아바타를 넣는다 — Humanoid 로 바꾼 뒤에도 프리팹이 클립을 받게 (기록 97번)
                { var anExisting = inst.GetComponent<Animator>(); if (anExisting != null && anExisting.avatar == null) { foreach (var o in AssetDatabase.LoadAllAssetsAtPath(modelPath)) { var av = o as Avatar; if (av != null) { anExisting.avatar = av; break; } } } if (anExisting != null) anExisting.applyRootMotion = false; }
                if (inst.GetComponent<Animator>() == null && inst.GetComponent<Animation>() == null)
                {
                    var an = inst.AddComponent<Animator>();
                    foreach (var o in AssetDatabase.LoadAllAssetsAtPath(modelPath)) { var av = o as Avatar; if (av != null) { an.avatar = av; break; } }
                }
                if (keepCulling.HasValue) { var anK = inst.GetComponent<Animator>(); if (anK != null) anK.cullingMode = keepCulling.Value; }
                bool ok;
                PrefabUtility.SaveAsPrefabAsset(inst, prefabPath, out ok);
                if (!ok) { rep.lines.Add("프리팹 저장 실패: " + prefabPath); return rep; }
                rep.prefabPath = prefabPath;
                rep.lines.Add("프리팹: " + prefabPath + (existed ? " (기존 프리팹을 덮어씀)" : ""));
                var removed = new List<string>();
                foreach (var g in prevGenerated)
                {
                    var ap = AssetDatabase.GUIDToAssetPath(g);
                    if (string.IsNullOrEmpty(ap) || used.Contains(ap) || !ap.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)) continue;
                    removed.Add(ap);
                }
                foreach (var ap in removed) AssetDatabase.DeleteAsset(ap);
                if (removed.Count > 0) rep.lines.Add("예전 실행의 쓰지 않는 에셋 " + removed.Count + "개 정리");
            }
            finally { UnityEngine.Object.DestroyImmediate(inst); foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t); }
            return rep;
        }

        class Slot { public Renderer rend; public int slot; public Material mat; public Texture albedo; public Mesh mesh; public bool sharpAll; public bool[] triSharp; }

        /// <summary>머리 본에 묶인 메시에서 '작은 연결 성분'(안구·이·입 안)의 삼각형을 표시한다. 머리 가중 넓이가 가장 큰 성분의 10% 미만이면 작은 성분. 없으면 null.</summary>
        static bool[] SmallHeadComponents(SkinnedMeshRenderer smr, Mesh baked, Transform headBone, Matrix4x4 l2w, out int count)
        {
            count = 0;
            var src = smr.sharedMesh; var bones = smr.bones; var bw = src.boneWeights; if (bw == null || bw.Length != src.vertexCount) return null;
            var isHead = new bool[bones.Length]; bool anyHead = false;
            for (int i = 0; i < bones.Length; i++) { isHead[i] = bones[i] != null && (bones[i] == headBone || bones[i].IsChildOf(headBone)); anyHead |= isHead[i]; }
            if (!anyHead) return null;
            var v = baked.vertices; int nv = v.Length; var hw = new float[nv];
            for (int i = 0; i < nv; i++)
            {
                var b = bw[i]; float w = 0f;
                if (b.boneIndex0 < isHead.Length && isHead[b.boneIndex0]) w += b.weight0; if (b.boneIndex1 < isHead.Length && isHead[b.boneIndex1]) w += b.weight1;
                if (b.boneIndex2 < isHead.Length && isHead[b.boneIndex2]) w += b.weight2; if (b.boneIndex3 < isHead.Length && isHead[b.boneIndex3]) w += b.weight3;
                hw[i] = w;
            }
            var parent = new int[nv]; for (int i = 0; i < nv; i++) parent[i] = i;
            var weld = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < nv; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(v[i].x * 20000f), Mathf.RoundToInt(v[i].y * 20000f), Mathf.RoundToInt(v[i].z * 20000f));
                int j; if (weld.TryGetValue(key, out j)) Union(parent, i, j); else weld[key] = i;
            }
            var tri = baked.triangles;
            for (int t = 0; t < tri.Length; t += 3) { Union(parent, tri[t], tri[t + 1]); Union(parent, tri[t], tri[t + 2]); }
            var area = new Dictionary<int, double>(); var compOf = new int[tri.Length / 3]; double maxA = 0;
            for (int t = 0; t < tri.Length; t += 3)
            {
                int r = Find(parent, tri[t]); compOf[t / 3] = r;
                var p0 = l2w.MultiplyPoint3x4(v[tri[t]]); var p1 = l2w.MultiplyPoint3x4(v[tri[t + 1]]); var p2 = l2w.MultiplyPoint3x4(v[tri[t + 2]]);
                float wm = (hw[tri[t]] + hw[tri[t + 1]] + hw[tri[t + 2]]) / 3f;
                double a = 0.5 * Vector3.Cross(p1 - p0, p2 - p0).magnitude * wm;
                double cur; area.TryGetValue(r, out cur); area[r] = cur + a; if (cur + a > maxA) maxA = cur + a;
            }
            if (maxA <= 0) return null;
            var small = new HashSet<int>(); foreach (var kv in area) if (kv.Value > 0 && kv.Value < 0.10 * maxA) small.Add(kv.Key);
            if (small.Count == 0) return null;
            var mark = new bool[tri.Length / 3]; for (int t = 0; t < mark.Length; t++) mark[t] = small.Contains(compOf[t]);
            count = small.Count;
            return mark;
        }
        /// <summary>0/1 마스크를 정사각 반경 r 로 넓힌다 (가로 최댓값 → 세로 최댓값)</summary>
        /// <summary>
        /// 피부 위 장식 되살리기 (기록 97번, 사용자 결정): The Boss 의 뺨 반창고처럼 피부 라벨 안에 그려진 작은 장식이 붓질·피부 스무딩에 지워지는 것을 막는다.
        /// 원본에서 (1) 피부 라벨(눈·눈썹 둘레 1×기준길이는 뺌)의 대표 색조를 구하고, (2) 피부색 축(대표 색조↔흰색)에서 벗어난 색조(perp &gt; 0.03)의 밝은 화소(밝기 &gt; 90)를 씨앗으로,
        /// (3) 씨앗 1.2×기준길이 안에서 색조 거리 &gt; 0.045 인 화소로 키우고, (4) 넓이 2~40×기준길이², 상자 채움 ≥ 0.35 인 덩어리만 원본으로 되돌린다(가장자리 흐림).
        /// 피부색 축을 따라 진하거나 옅어진 것(코·귀·눈가 그늘·이마 하이라이트·수염)은 씨앗이 되지 않는다 — 텍스처 시제품(decal_proto3)에서 확인. 반환: 덩어리 수.
        /// </summary>
        static int RestoreDecals(byte[] rgba, byte[] orig, int W, int H, FBX2NPRParseBake.TexResult tr, float hw, float strength, out int texN)
        {
            int N = W * H; texN = 0;
            var skin = new bool[N]; var feat = new float[N]; var ear = new float[N];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                byte L = tr.label[((2 * y + 1) * tr.H / (2 * H)) * tr.W + ((2 * x + 1) * tr.W / (2 * W))];
                if (L == FaceParser.Skin) skin[y * W + x] = true;
                else if (L == FaceParser.LEye || L == FaceParser.REye || L == FaceParser.LBrow || L == FaceParser.RBrow) feat[y * W + x] = 1f;
                else if (L == FaceParser.LEar || L == FaceParser.REar || L == FaceParser.EarR) ear[y * W + x] = 1f;
            }
            feat = Dilate(feat, W, H, Mathf.Max(1, Mathf.RoundToInt(hw)));
            ear = Dilate(ear, W, H, Mathf.Max(1, Mathf.RoundToInt(0.5f * hw)));   // 귀 근처 덩어리는 버린다 — 파서가 귀 일부를 피부로 라벨해 귀 안쪽이 되살아났다(1차)
            var lr = new List<float>(); var lg = new List<float>(); var cr = new float[N]; var cg = new float[N]; var lum = new float[N];
            for (int i = 0; i < N; i++)
            {
                if (skin[i] && feat[i] > 0f) skin[i] = false;
                if (!skin[i]) continue;
                float R = orig[i * 4], G = orig[i * 4 + 1], B = orig[i * 4 + 2], sum = R + G + B; if (sum < 60f) { skin[i] = false; continue; }
                cr[i] = R / sum; cg[i] = G / sum; lum[i] = 0.2126f * R + 0.7152f * G + 0.0722f * B; lr.Add(cr[i]); lg.Add(cg[i]);
            }
            if (lr.Count < 100) return 0;
            lr.Sort(); lg.Sort(); float mr = lr[lr.Count / 2], mg = lg[lg.Count / 2];
            var ll = new List<float>(); for (int i = 0; i < N; i++) if (skin[i]) ll.Add(lum[i]); ll.Sort(); float ml = ll[ll.Count / 2];
            float ax = 1f / 3f - mr, ay = 1f / 3f - mg, al = Mathf.Sqrt(ax * ax + ay * ay); if (al < 1e-4f) return 0; ax /= al; ay /= al;
            var dist = new float[N]; var perp = new float[N]; var seed = new float[N];
            for (int i = 0; i < N; i++)
            {
                if (!skin[i]) continue;
                float dx = cr[i] - mr, dy = cg[i] - mg; dist[i] = Mathf.Sqrt(dx * dx + dy * dy);
                float a = dx * ax + dy * ay, px = dx - a * ax, py = dy - a * ay; perp[i] = Mathf.Sqrt(px * px + py * py);
                if (perp[i] > 0.03f && lum[i] > 90f) seed[i] = 1f;
            }
            var near = Dilate(seed, W, H, Mathf.Max(1, Mathf.RoundToInt(1.2f * hw)));
            // 키우는 곳: 색조가 조금이라도 다르거나(테이프) 피부보다 확실히 밝은(패드) 화소만. 피부보다 진한 같은 색 계열(눈 밑 그늘)은 안 키운다 — 1차에서 눈 밑 그늘이 딸려 왔다
            var m = new bool[N]; for (int i = 0; i < N; i++) m[i] = skin[i] && (seed[i] > 0f || (near[i] > 0f && dist[i] > 0.045f && (perp[i] > 0.015f || lum[i] > ml + 25f)));
            var lab = new int[N]; var stack = new int[N]; var keep = new float[N]; int id = 0, kept = 0;
            double aMin = 2.0 * hw * hw, aMax = 40.0 * hw * hw;
            for (int s0 = 0; s0 < N; s0++)
            {
                if (!m[s0] || lab[s0] != 0) continue;
                id++; int sp = 0; stack[sp++] = s0; lab[s0] = id; var mem = new List<int>(); int bx0 = W, by0 = H, bx1 = -1, by1 = -1, seeds = 0;
                while (sp > 0)
                {
                    int q = stack[--sp]; mem.Add(q); if (seed[q] > 0f) seeds++; int x = q % W, y = q / W;
                    if (x < bx0) bx0 = x; if (x > bx1) bx1 = x; if (y < by0) by0 = y; if (y > by1) by1 = y;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                        int r = yy * W + xx; if (m[r] && lab[r] == 0) { lab[r] = id; stack[sp++] = r; }
                    }
                }
                if (seeds == 0) continue;
                double area = mem.Count, fill = area / ((bx1 - bx0 + 1.0) * (by1 - by0 + 1.0));
                if (area < aMin || area > aMax || fill < 0.35) continue;
                int onEar = 0; foreach (var q in mem) if (ear[q] > 0f) onEar++;
                if (onEar > 0.05 * area) continue;
                kept++; foreach (var q in mem) keep[q] = 1f;
            }
            if (kept == 0) return 0;
            keep = Dilate(keep, W, H, 1);
            keep = PainterlyCore.BlurMask(keep, W, H, Math.Max(1.0, 0.15 * hw));
            for (int i = 0; i < N; i++)
            {
                float w = Mathf.Clamp01(keep[i] * 1.5f) * strength; if (w <= 0.001f) continue;
                if (w > 0.5f) texN++;
                for (int k = 0; k < 3; k++) rgba[i * 4 + k] = (byte)Mathf.RoundToInt(rgba[i * 4 + k] + (orig[i * 4 + k] - rgba[i * 4 + k]) * w);
            }
            return kept;
        }

        static float[] Dilate(float[] m, int W, int H, int r)
        {
            var t = new float[W * H]; var o = new float[W * H];
            for (int y = 0; y < H; y++) { int row = y * W; for (int x = 0; x < W; x++) { float mx = 0f; for (int xx = Math.Max(0, x - r); xx <= Math.Min(W - 1, x + r); xx++) if (m[row + xx] > mx) { mx = m[row + xx]; if (mx >= 1f) break; } t[row + x] = mx; } }
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++) { float mx = 0f; for (int yy = Math.Max(0, y - r); yy <= Math.Min(H - 1, y + r); yy++) if (t[yy * W + x] > mx) { mx = t[yy * W + x]; if (mx >= 1f) break; } o[y * W + x] = mx; }
            return o;
        }
        static int Find(int[] p, int i) { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }
        static void Union(int[] p, int a, int b) { a = Find(p, a); b = Find(p, b); if (a != b) p[a] = b; }

        /// <summary>서브메시의 삼각형(triOk 가 null 이면 전부, 아니면 전체 메시 삼각형 번호 기준으로 표시된 것만)을 UV 공간에 그려 mask 를 1 로 채운다.</summary>
        static void RasterTris(float[] mask, int W, int H, Mesh mesh, int sub, bool[] triOk)
        {
            var uv = mesh.uv; if (uv == null || uv.Length != mesh.vertexCount) return;
            var idx = mesh.GetTriangles(sub); int triBase = 0;
            for (int s = 0; s < sub; s++) triBase += (int)mesh.GetIndexCount(s) / 3;   // triOk 는 전체 삼각형 순서(서브메시 순)
            for (int t = 0; t < idx.Length; t += 3)
            {
                if (triOk != null) { int gt = triBase + t / 3; if (gt >= triOk.Length || !triOk[gt]) continue; }
                Vector2 a = uv[idx[t]], b = uv[idx[t + 1]], c = uv[idx[t + 2]];
                var off = new Vector2(Mathf.Floor(Mathf.Min(a.x, Mathf.Min(b.x, c.x))), Mathf.Floor(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                a -= off; b -= off; c -= off;
                if (Mathf.Max(a.x, Mathf.Max(b.x, c.x)) > 1.0001f || Mathf.Max(a.y, Mathf.Max(b.y, c.y)) > 1.0001f) continue;
                float ax = a.x * W, ay = a.y * H, bx = b.x * W, by = b.y * H, cx = c.x * W, cy = c.y * H;
                float det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay); if (Mathf.Abs(det) < 1e-6f) continue;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx)))), x1 = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(ax, Mathf.Max(bx, cx))));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy)))), y1 = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(ay, Mathf.Max(by, cy))));
                float inv = 1f / det;
                for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                {
                    float qx = x + 0.5f, qy = y + 0.5f;
                    float l1 = ((qx - ax) * (cy - ay) - (cx - ax) * (qy - ay)) * inv, l2 = ((bx - ax) * (qy - ay) - (qx - ax) * (by - ay)) * inv, l0 = 1f - l1 - l2;
                    if (l0 < -1e-4f || l1 < -1e-4f || l2 < -1e-4f) continue;
                    mask[y * W + x] = 1f;
                }
            }
        }

        /// <summary>텍셀 밀도용: 이 서브메시 삼각형들의 UV 넓이(텍셀²)와 월드 넓이(m²)를 텍스처별로 더한다</summary>
        static void Accumulate(Dictionary<Texture, double[]> acc, Texture tex, Mesh mesh, int sub, Matrix4x4 l2w)
        {
            var v = mesh.vertices; var uv = mesh.uv; var tri = mesh.GetTriangles(sub);
            if (uv == null || uv.Length != v.Length) return;
            double[] a; if (!acc.TryGetValue(tex, out a)) acc[tex] = a = new double[2];
            int W = tex.width, H = tex.height;
            for (int t = 0; t < tri.Length; t += 3)
            {
                int i0 = tri[t], i1 = tri[t + 1], i2 = tri[t + 2];
                Vector2 p = uv[i0], q = uv[i1], r = uv[i2];
                float det = (q.x - p.x) * W * (r.y - p.y) * H - (r.x - p.x) * W * (q.y - p.y) * H;
                var P0 = l2w.MultiplyPoint3x4(v[i0]); var P1 = l2w.MultiplyPoint3x4(v[i1]); var P2 = l2w.MultiplyPoint3x4(v[i2]);
                a[0] += 0.5 * Math.Abs(det); a[1] += 0.5 * Vector3.Cross(P1 - P0, P2 - P0).magnitude;
            }
        }

        static string TexBase(string dir, Texture tex, Dictionary<string, Texture> names)
        {
            string b = FBX2NPRConverter.Safe(tex.name); Texture other;
            if (names.TryGetValue(b, out other) && other != tex) b += "_" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(tex)).Substring(0, 8);
            names[b] = tex;
            return dir + "/Textures/" + b;
        }
    }
}
