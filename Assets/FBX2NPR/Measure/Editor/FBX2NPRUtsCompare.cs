// FBX2NPRUtsCompare.cs — 유니티짱 얼굴을 네 가지로 같은 무대·같은 디렉셔널 라이트·같은 카메라(FBX2NPRFinalSheet 얼굴 컷)에서 찍는다.
//   (1) 원본 PBR (URP Lit, FixOriginal 로 텍스처 연결)  (2) 공식 UTS(Toon/Toon) · 원본 FBX 노멀
//   (3) 공식 UTS · FBX2NPR 툰 프리팹의 정리된 노멀 메시  (4) FBX2NPR 최종 툰 (FinalSheet 순서 그대로, 외곽선 포함)
// 프리팹·재질·FBX·.meta 는 바꾸지 않는다. UTS 재질·클리핑 마스크 텍스처는 임시로 만들고 끝나면 지운다. 결과 NormalTestOut/uts_compare/.
// UTS 값의 출처: com.unity.toonshader 0.15.1 의 Samples~/URP/UnityChan/SD/SampleMaterials (SD 유니티짱용. 2014 표준 모델용 공식 UTS 재질은 없다).
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRUtsCompare.Run
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace FBX2NPR.EditorTools
{
    public static partial class FBX2NPRUtsCompare   // partial: Baked Normal 시험(RunBaked)은 FBX2NPRUtsBaked.cs
    {
        const string ModelName = "unitychan";
        const float FovY = 24f;          // FBX2NPRCompare.Shoot 의 FovY 와 같은 값 (빛 확인용 구에서 카메라를 다시 세울 때)
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "uts_compare"); } }
        static readonly List<string> Log = new List<string>();
        static void L(string s) { Log.Add(s); Debug.Log("[UtsCompare] " + s); }
        static string F(float v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
        static string V3(Vector3 v) { return "(" + F(v.x) + ", " + F(v.y) + ", " + F(v.z) + ")"; }
        static string C3(Color c) { return "(" + F(c.r) + ", " + F(c.g) + ", " + F(c.b) + ")"; }

        enum Kind { FaceSkin, Hair, Body, Detail }

        // 공식 SD 유니티짱 샘플 재질의 외곽선 값 (부위별로 가장 가까운 공식 재질)
        struct OutlineSpec { public float width; public Color color; public float blendBase; public float unlit; public string from; }
        static OutlineSpec OutlineOf(Kind k)
        {
            switch (k)
            {
                case Kind.FaceSkin: return new OutlineSpec { width = 5f, color = new Color(0.3602941f, 0.3602941f, 0.3602941f, 1f), blendBase = 1f, unlit = 2f, from = "ToonShader_Face" };
                case Kind.Hair: return new OutlineSpec { width = 4.3f, color = new Color(0.4044118f, 0.4044118f, 0.4044118f, 1f), blendBase = 1f, unlit = 2f, from = "ToonShader_Hair_Clipping" };
                case Kind.Body: return new OutlineSpec { width = 2.9f, color = new Color(0.19117647f, 0.19117647f, 0.19117647f, 1f), blendBase = 1f, unlit = 2f, from = "ToonShader_Main" };
                default: return new OutlineSpec { width = 0f, color = new Color(0.5f, 0.5f, 0.5f, 1f), blendBase = 0f, unlit = 0.001f, from = "ToonShader_Eye_Clipping_StencilMask (외곽선 0)" };
            }
        }

        static Kind KindOf(string matName, FBX2NPRAssetSettings crit)
        {
            string n = (matName ?? "").ToLowerInvariant();
            foreach (var d in new[] { "eye", "cheek", "brow", "lash", "mouth", "teeth", "tooth" }) if (n.Contains(d)) return Kind.Detail;
            if (n.Contains("hair")) return Kind.Hair;
            foreach (var k in (crit.faceMaterialContains ?? "").Split(';')) { var t = k.Trim().ToLowerInvariant(); if (t.Length > 0 && n.Contains(t)) return Kind.FaceSkin; }
            return Kind.Body;
        }

        static string FindModel(string name)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }
        static string RelPath(Transform t, Transform root)
        {
            var parts = new List<string>();
            while (t != null && t != root) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts.ToArray());
        }
        static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer; if (smr != null) return smr.sharedMesh;
            var mf = r.GetComponent<MeshFilter>(); return mf != null ? mf.sharedMesh : null;
        }
        // 리니어 값으로 곱하고 싶은 색을 SetColor 에 넣을 값으로 (리니어 프로젝트에서 Color 프로퍼티는 sRGB 로 보고 리니어로 바꿔 올린다)
        static Color ForSetColor(Color linear) { return QualitySettings.activeColorSpace == ColorSpace.Linear ? linear.gamma : linear; }

        // 베이스 텍스처의 알파를 R 에 담은 마스크 (UTS 의 _IS_CLIPPING_MODE 는 _ClippingMask 의 R 만 읽는다)
        static Texture2D AlphaToR(Texture src, List<UnityEngine.Object> temps)
        {
            int w = src.width, h = src.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            t.ReadPixels(new Rect(0, 0, w, h), 0, 0); t.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = t.GetPixels32(); UnityEngine.Object.DestroyImmediate(t);
            int cut = 0;
            for (int i = 0; i < px.Length; i++) { byte a = px[i].a; if (a < 128) cut++; px[i] = new Color32(a, a, a, a); }
            var m = new Texture2D(w, h, TextureFormat.RGBA32, true, true) { name = src.name + "_alphaR" };
            m.SetPixels32(px); m.Apply(true, false); m.wrapMode = src.wrapMode; m.filterMode = FilterMode.Bilinear;
            temps.Add(m);
            L("    클리핑 마스크 " + m.name + ": " + w + "x" + h + ", 알파<0.5 화소 " + (100.0 * cut / Math.Max(1, px.Length)).ToString("F1", CultureInfo.InvariantCulture) + "%");
            return m;
        }

        static bool HasAlpha(Texture tex)
        {
            if (tex == null) return false;
            var ti = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) as TextureImporter;
            return ti != null && ti.DoesSourceTextureHaveAlpha();
        }

        static Material MakeUts(Material src, Texture tex, Kind kind, Color shadeLinear, List<UnityEngine.Object> temps)
        {
            var sh = Shader.Find("Toon/Toon");
            var m = new Material(sh) { name = "UTS_" + src.name };
            temps.Add(m);
            var t = tex != null ? tex : Texture2D.whiteTexture;
            m.SetTexture("_MainTex", t); m.SetTexture("_BaseMap", t);
            m.SetColor("_BaseColor", Color.white); m.SetColor("_Color", Color.white);
            // 기법: DoubleShadeWithFeather
            m.SetFloat("_utsTechnique", 0f); m.DisableKeyword("_SHADINGGRADEMAP");
            // 1영색 = 기본 텍스처 × 그림자 색 (공식 재질의 손그림 어두운 텍스처 대신). 2영색도 1영 텍스처를 쓴다(_Use_1stAs2nd, 공식 값과 같음)
            m.SetFloat("_Use_BaseAs1st", 1f); m.SetFloat("_Use_1stAs2nd", 1f);
            m.SetColor("_1st_ShadeColor", ForSetColor(shadeLinear));
            if (kind == Kind.FaceSkin)
            {
                // 공식 SD 유니티짱 ToonShader_Face.mat 의 명암 값
                m.SetFloat("_BaseColor_Step", 0.353f); m.SetFloat("_BaseShade_Feather", 0.121f);
                m.SetFloat("_ShadeColor_Step", 0f); m.SetFloat("_1st2nd_Shades_Feather", 0.0001f);
                m.SetFloat("_1st_ShadeColor_Step", 0.353f); m.SetFloat("_1st_ShadeColor_Feather", 0.121f);
                m.SetFloat("_2nd_ShadeColor_Step", 0f); m.SetFloat("_2nd_ShadeColor_Feather", 0.0001f);
                m.SetFloat("_Is_NormalMapToBase", 0f); m.SetFloat("_Set_SystemShadowsToBase", 1f); m.SetFloat("_Tweak_SystemShadowsLevel", -0.19f);
                m.SetColor("_2nd_ShadeColor", new Color(0.7490196f, 0.69411767f, 0.88235295f, 1f));
                m.SetFloat("_Is_Filter_LightColor", 0f);
            }
            // 몸·머리카락·눈 등은 Step/Feather 를 UTS 기본값 그대로 둔다
            // 림라이트·하이컬러·매트캡·GI 는 끈다 (명암 '모양' 비교용)
            m.SetFloat("_RimLight", 0f); m.SetFloat("_HighColor_Power", 0f); m.SetColor("_HighColor", Color.black);
            m.SetFloat("_MatCap", 0f); m.SetFloat("_AngelRing", 0f); m.SetFloat("_GI_Intensity", 0f); m.SetFloat("_Is_BLD", 0f);
            m.EnableKeyword("_EMISSIVE_SIMPLE"); m.DisableKeyword("_EMISSIVE_ANIMATION");
            m.EnableKeyword("_IS_TRANSCLIPPING_OFF"); m.DisableKeyword("_IS_TRANSCLIPPING_ON");
            // 외곽선 (반전 헐) — 공식 샘플 값
            var os = OutlineOf(kind);
            m.EnableKeyword("_OUTLINE_NML"); m.DisableKeyword("_OUTLINE_POS"); m.SetFloat("_OUTLINE", 0f);
            m.SetFloat("_Outline_Width", os.width); m.SetColor("_Outline_Color", os.color); m.SetFloat("_Is_BlendBaseColor", os.blendBase);
            m.SetFloat("_Is_LightColor_Outline", 1f); m.SetFloat("_Unlit_Intensity", os.unlit);
            m.SetFloat("_Farthest_Distance", 10f); m.SetFloat("_Nearest_Distance", 0.5f);
            m.SetFloat("_Is_OutlineTex", 0f); m.SetFloat("_Is_BakedNormal", 0f); m.SetFloat("_Offset_Z", 0f);
            m.SetShaderPassEnabled("SRPDefaultUnlit", os.width > 0f);
            // 알파 컷아웃: 알파를 R 에 담은 마스크 + _IS_CLIPPING_MODE (외곽선 패스도 같은 마스크로)
            bool clip = HasAlpha(tex);
            m.DisableKeyword("_IS_CLIPPING_OFF"); m.DisableKeyword("_IS_CLIPPING_MODE"); m.DisableKeyword("_IS_CLIPPING_TRANSMODE");
            m.DisableKeyword("_IS_OUTLINE_CLIPPING_NO"); m.DisableKeyword("_IS_OUTLINE_CLIPPING_YES");
            if (clip)
            {
                m.SetFloat("_ClippingMode", 1f); m.EnableKeyword("_IS_CLIPPING_MODE"); m.EnableKeyword("_IS_OUTLINE_CLIPPING_YES");
                m.SetTexture("_ClippingMask", AlphaToR(tex, temps)); m.SetFloat("_IsBaseMapAlphaAsClippingMask", 0f);
                m.SetFloat("_Clipping_Level", 0f); m.SetFloat("_Inverse_Clipping", 0f);
                m.SetFloat("_CullMode", 0f);
                m.renderQueue = (int)RenderQueue.AlphaTest; m.SetOverrideTag("RenderType", "TransparentCutout");
            }
            else
            {
                m.SetFloat("_ClippingMode", 0f); m.EnableKeyword("_IS_CLIPPING_OFF"); m.EnableKeyword("_IS_OUTLINE_CLIPPING_NO");
                m.SetFloat("_CullMode", 2f);
                m.renderQueue = (int)RenderQueue.Geometry; m.SetOverrideTag("RenderType", "Opaque");
            }
            var s1 = m.GetColor("_1st_ShadeColor");
            L(string.Format(CultureInfo.InvariantCulture,
                "  UTS 재질 {0} [{1}] tex={2} 클리핑={3} | BaseColor_Step {4} BaseShade_Feather {5} ShadeColor_Step {6} 1st2nd_Feather {7} SystemShadowsToBase {8} Tweak_SystemShadowsLevel {9} NormalMapToBase {10} | 1st_ShadeColor 저장값 {11} = 리니어 {12} (BaseAs1st {13}) 2nd_ShadeColor {14} (1stAs2nd {15}) | 외곽선 폭 {16} 색 {17} BlendBaseColor {18} LightColor_Outline {19} Unlit_Intensity {20} ({21}) 패스 {22} | Cull {23} 큐 {24} | Rim {25} HighColor_Power {26} GI {27} Filter_LightColor {28}",
                m.name, kind, t.name, clip, F(m.GetFloat("_BaseColor_Step")), F(m.GetFloat("_BaseShade_Feather")), F(m.GetFloat("_ShadeColor_Step")), F(m.GetFloat("_1st2nd_Shades_Feather")),
                F(m.GetFloat("_Set_SystemShadowsToBase")), F(m.GetFloat("_Tweak_SystemShadowsLevel")), F(m.GetFloat("_Is_NormalMapToBase")),
                C3(s1), C3(s1.linear), F(m.GetFloat("_Use_BaseAs1st")), C3(m.GetColor("_2nd_ShadeColor")), F(m.GetFloat("_Use_1stAs2nd")),
                F(m.GetFloat("_Outline_Width")), C3(m.GetColor("_Outline_Color")), F(m.GetFloat("_Is_BlendBaseColor")), F(m.GetFloat("_Is_LightColor_Outline")), F(m.GetFloat("_Unlit_Intensity")), os.from,
                m.GetShaderPassEnabled("SRPDefaultUnlit") ? "켬" : "끔", F(m.GetFloat("_CullMode")), m.renderQueue,
                F(m.GetFloat("_RimLight")), F(m.GetFloat("_HighColor_Power")), F(m.GetFloat("_GI_Intensity")), F(m.GetFloat("_Is_Filter_LightColor"))));
            return m;
        }

        public static void Run()
        {
            Directory.CreateDirectory(OutDir); Log.Clear();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            var temps = new List<UnityEngine.Object>();
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                var utsShader = Shader.Find("Toon/Toon");
                L("UTS 셰이더 " + (utsShader != null ? utsShader.name + " (" + AssetDatabase.GetAssetPath(utsShader) + ") 지원=" + utsShader.isSupported : "없음"));
                if (utsShader == null) return;
                string mp = FindModel(ModelName); L("모델 " + mp);
                var crit = FBX2NPRRuntimeCheck.Crit(ModelName);
                string toonPath = FBX2NPRRuntimeCheck.PrefabPath(ModelName + "_Toon"); L("툰 프리팹 " + toonPath);
                L("색 공간 " + QualitySettings.activeColorSpace);

                // ---- 무대 (FinalSheet 와 같은 NewStage(false))
                FBX2NPRRuntimeCheck.NewStage(false);
                var sun = RenderSettings.sun;
                L("디렉셔널 라이트: 회전 " + V3(sun.transform.rotation.eulerAngles) + ", 진행 방향 " + V3(sun.transform.forward) + ", 빛이 오는 쪽 " + V3(-sun.transform.forward)
                  + " (FBX2NPRConverter.LightDir(35,25) = " + V3(FBX2NPRConverter.LightDir(35f, 25f)) + "), 세기 " + F(sun.intensity) + ", 색 " + C3(sun.color) + ", 그림자 " + sun.shadows + ", RenderSettings.sun=" + (RenderSettings.sun == sun));
                L("주변광: 모드 " + RenderSettings.ambientMode + ", 색(저장값 sRGB) " + C3(RenderSettings.ambientLight) + " = 리니어 " + C3(RenderSettings.ambientLight.linear) + ", 세기 " + F(RenderSettings.ambientIntensity) + ", 반사 세기 " + F(RenderSettings.reflectionIntensity));

                // ---- 빛 확인: UTS 재질 구 하나를 찍어 명암 경계가 이 라이트 방향의 N·L=0 과 맞는지 화소 단위로 대조
                LightProbe(sun, temps);

                // ---- (1) 원본 PBR
                var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));
                pbr.name = "cell1_orig";
                foreach (var n in OriginalMaterialFix.Apply(pbr, crit, mp, temps)) L("(1) " + n);
                Bounds head; int hv; if (!FBX2NPRParseBake.HeadBounds(pbr, crit, out head, out hv)) head = FBX2NPRRuntimeCheck.HeadOf(pbr, ModelName, new List<string>());
                L("머리 바운드 (원본 FBX 기준, FinalSheet 와 같음): 중심 " + V3(head.center) + " 크기 " + V3(head.size) + " 정점 " + hv);

                // ---- (2) 공식 UTS · 원본 노멀: FBX 를 하나 더 세우고 모든 재질을 UTS 임시 재질로
                var utsFbx = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));
                utsFbx.name = "cell2_uts_orignormal";
                var rig0 = AssetDatabase.LoadAssetAtPath<GameObject>(toonPath).GetComponentInChildren<ToonFaceRig>(true);
                Color skinShade = rig0 != null ? rig0.skinShadow : ToonFaceRig.DefaultSkinShadow;
                Color bodyShade = rig0 != null ? rig0.bodyShadowColor : ToonFaceRig.DefaultSkinShadow;
                L("그림자 색(FBX2NPR 툰 프리팹 리그 값, 리니어 곱): 피부 " + C3(skinShade) + ", 몸 " + C3(bodyShade) + " (기본 피부 그림자 " + C3(ToonFaceRig.DefaultSkinShadow) + ")");
                var probeDefault = new Material(utsShader); temps.Add(probeDefault);
                L("UTS 새 재질 기본값 (몸·머리카락·눈에 그대로 둔 값): BaseColor_Step " + F(probeDefault.GetFloat("_BaseColor_Step")) + ", BaseShade_Feather " + F(probeDefault.GetFloat("_BaseShade_Feather"))
                  + ", ShadeColor_Step " + F(probeDefault.GetFloat("_ShadeColor_Step")) + ", 1st2nd_Shades_Feather " + F(probeDefault.GetFloat("_1st2nd_Shades_Feather"))
                  + ", Set_SystemShadowsToBase " + F(probeDefault.GetFloat("_Set_SystemShadowsToBase")) + ", Tweak_SystemShadowsLevel " + F(probeDefault.GetFloat("_Tweak_SystemShadowsLevel"))
                  + ", 2nd_ShadeColor " + C3(probeDefault.GetColor("_2nd_ShadeColor")) + ", Unlit_Intensity " + F(probeDefault.GetFloat("_Unlit_Intensity")));

                var utsOf = new Dictionary<Material, Material>();
                var srcMatsByPath = new Dictionary<string, Material[]>();
                var fbxStateByPath = new Dictionary<string, bool>();
                var fbxMeshByPath = new Dictionary<string, Mesh>();
                L("(2) 재질 교체:");
                foreach (var r in utsFbx.GetComponentsInChildren<Renderer>(true))
                {
                    string path = RelPath(r.transform, utsFbx.transform);
                    var mesh = MeshOf(r);
                    if (mesh != null && crit.IsIgnoredMesh(mesh.name)) { r.enabled = false; L("  끔(원본 쪽과 같이 데칼 메시): " + path); }
                    var src = r.sharedMaterials; srcMatsByPath[path] = src; fbxMeshByPath[path] = mesh;
                    fbxStateByPath[path] = r.enabled && r.gameObject.activeSelf;
                    var dst = new Material[src.Length];
                    for (int i = 0; i < src.Length; i++)
                    {
                        if (src[i] == null) continue;
                        Material u;
                        if (!utsOf.TryGetValue(src[i], out u))
                        {
                            var tex = src[i].mainTexture != null ? src[i].mainTexture : FBX2NPRConverter.ResolveAlbedo(src[i], mp, crit);
                            var kind = KindOf(src[i].name, crit);
                            u = MakeUts(src[i], tex, kind, kind == Kind.FaceSkin ? skinShade : bodyShade, temps);
                            utsOf[src[i]] = u;
                        }
                        dst[i] = u;
                    }
                    r.sharedMaterials = dst;
                    var smr = r as SkinnedMeshRenderer;
                    var names = new List<string>(); foreach (var m in src) names.Add(m != null ? m.name : "null");
                    L("  " + path + " (" + (smr != null ? "skinned" : "static") + ", 켜짐 " + fbxStateByPath[path] + ", 메시 " + (mesh != null ? mesh.name + " v" + mesh.vertexCount : "-") + ") 재질 " + string.Join(",", names.ToArray())
                      + " | lossyScale " + V3(r.transform.lossyScale) + (smr != null && smr.rootBone != null ? " rootBone " + smr.rootBone.name + " lossyScale " + V3(smr.rootBone.lossyScale) : ""));
                }

                // ---- (3) 공식 UTS · FBX2NPR 노멀: 툰 프리팹 인스턴스에서 리그를 끄고 프로퍼티 블록을 지운 뒤 렌더러 재질만 (2)와 같은 UTS 재질로
                var utsToon = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(toonPath));
                utsToon.name = "cell3_uts_toonkitnormal";
                var types = new SortedSet<string>(); foreach (var mb in utsToon.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) types.Add(mb.GetType().Name);
                L("(3) 툰 프리팹의 스크립트 컴포넌트: " + string.Join(", ", new List<string>(types).ToArray()));
                foreach (var mb in utsToon.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null) mb.enabled = false;   // 리그가 프로퍼티 블록에 _RimLight 등을 넣지 않게
                L("(3) 재질 교체·켜짐 맞춤:");
                foreach (var r in utsToon.GetComponentsInChildren<Renderer>(true))
                {
                    r.SetPropertyBlock(null);
                    var emptyBlock = new MaterialPropertyBlock();
                    for (int i = 0; i < r.sharedMaterials.Length; i++) { try { r.SetPropertyBlock(null, i); } catch { r.SetPropertyBlock(emptyBlock, i); } }
                    string path = RelPath(r.transform, utsToon.transform);
                    if (!srcMatsByPath.ContainsKey(path))
                    {
                        // 계층 깊이가 다르면 이름이 하나뿐인 렌더러끼리 맞춘다
                        string only = null; int hits = 0;
                        foreach (var kv in srcMatsByPath) { var nm = kv.Key.Substring(kv.Key.LastIndexOf('/') + 1); if (nm == r.name) { only = kv.Key; hits++; } }
                        if (hits == 1) { L("  (경로 " + path + " → 이름으로 맞춤 " + only + ")"); path = only; }
                    }
                    var tmesh = MeshOf(r);
                    var toonMats = r.sharedMaterials; var tn = new List<string>(); foreach (var m in toonMats) tn.Add(m != null ? m.name : "null");
                    bool toonOn = r.enabled && r.gameObject.activeSelf;
                    Material[] src; bool want;
                    if (!srcMatsByPath.TryGetValue(path, out src))
                    {
                        r.enabled = false; L("  " + path + ": 원본 FBX 에 없는 렌더러 → 끔 (툰 재질 " + string.Join(",", tn.ToArray()) + ")"); continue;
                    }
                    want = fbxStateByPath[path];
                    var dst = new Material[toonMats.Length];
                    string mapNote = "";
                    for (int i = 0; i < toonMats.Length; i++)
                    {
                        Material s = i < src.Length ? src[i] : null;
                        if (src.Length != toonMats.Length && toonMats[i] != null)
                            foreach (var cand in src) if (cand != null && toonMats[i].name.StartsWith(cand.name, StringComparison.OrdinalIgnoreCase)) { s = cand; mapNote = " (칸 수가 달라 이름으로 맞춤)"; }
                        Material u; dst[i] = s != null && utsOf.TryGetValue(s, out u) ? u : null;
                    }
                    r.sharedMaterials = dst;
                    if (toonOn != want) { r.gameObject.SetActive(want || r.gameObject.activeSelf); r.enabled = want; }
                    var dn = new List<string>(); foreach (var m in dst) dn.Add(m != null ? m.name : "null");
                    string meshNote = MeshCompare(fbxMeshByPath.ContainsKey(path) ? fbxMeshByPath[path] : null, tmesh);
                    L("  " + path + ": 툰 재질 " + string.Join(",", tn.ToArray()) + " → " + string.Join(",", dn.ToArray()) + mapNote + " | 켜짐 툰 " + toonOn + " → " + want + (toonOn != want ? " (원본 쪽과 맞춤)" : "") + " | " + meshNote);
                }

                // ---- (4) FBX2NPR 툰 (FinalSheet 순서)
                var toon = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(toonPath));
                toon.name = "cell4_toonkit";
                var trig = toon.GetComponentInChildren<ToonFaceRig>(true);
                var frn = new List<string>(); foreach (var r in trig.faceRenderers) frn.Add(r != null ? r.name : "null");
                L("(4) 리그: 빛 표식 " + (trig.lightTransform != null ? trig.lightTransform.name : "없음(씬 디렉셔널 라이트를 따름)") + ", 명암선 위치 " + F(trig.linePosition) + " 부드러움 " + F(trig.lineSoftness)
                  + ", 피부 그림자 " + C3(trig.skinShadow) + ", 몸 명암 " + F(trig.bodyShadow) + " 색 " + C3(trig.bodyShadowColor) + ", 림 " + F(trig.rim)
                  + ", 외곽선 " + trig.outline + " 두께 " + F(trig.outlineThickness) + " darkness " + F(trig.outlineDarkness) + ", 얼굴 렌더러 " + string.Join(",", frn.ToArray())
                  + ", faceMaterials " + trig.faceMaterials.Count + " otherMaterials " + trig.otherMaterials.Count);

                // ---- 찍기
                var gos = new[] { pbr, utsFbx, utsToon, toon };
                var files = new[] { "unitychan_1_orig_pbr.png", "unitychan_2_uts_orignormal.png", "unitychan_3_uts_toonkitnormal.png", "unitychan_4_toonkit.png" };
                // 버리는 촬영: 1차 실행에서 UTS 칸의 첫 촬영만 홍채(알파 클리핑 재질)의 클리핑이 안 먹어 흰자가 연두색으로 찍혔다(같은 칸을 맨 끝에 다시 찍으면 정상).
                // 원인은 확인하지 못했다. (2)(3)을 한 번씩 먼저 그려 두고 본 촬영을 한다. 맨 끝의 '다시 찍기' 차이로 효과를 확인한다.
                string warm = Path.Combine(Path.GetTempPath(), "toonkit_uts_warmup.png");
                foreach (var wk in new[] { 1, 2 })
                {
                    foreach (var g in gos) g.SetActive(g == gos[wk]);
                    if (feat != null) feat.SetActive(false);
                    FBX2NPRCompare.SetSS(2);
                    FBX2NPRCompare.Shoot(head, 700, 700, warm, Vector3.forward, false, 1.18f);
                }
                if (File.Exists(warm)) File.Delete(warm);
                L("버리는 촬영 (2)(3) 한 번씩 마침");
                // 얼굴 외곽선의 예상 화면 폭: UTS 는 오브젝트 공간에서 노멀 × 폭 × 0.001 × smoothstep(Farthest, Nearest, 오브젝트 원점-카메라 거리) 만큼 민다
                {
                    float halfH = Mathf.Max(head.size.y * 0.5f, head.size.x * 0.5f);
                    float dist = halfH / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * 1.18f + head.size.z * 0.5f;
                    var camPos = head.center + Vector3.forward * dist;
                    float pxPerM = 700f / (2f * dist * Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad));   // 머리 중심 깊이에서
                    foreach (var r in utsFbx.GetComponentsInChildren<Renderer>(true))
                    {
                        if (r.name != "EYE_DEF") continue;   // 정적 메시(오브젝트 원점이 정확). 스킨 메시는 원점이 루트 본 쪽이라 빼 둔다
                        var mat = r.sharedMaterial; float wdt = mat.GetFloat("_Outline_Width");
                        float d = Vector3.Distance(r.transform.position, camPos);
                        float t = Mathf.Clamp01((d - 10f) / (0.5f - 10f)); float sm = t * t * (3f - 2f * t);
                        float worldW = wdt * 0.001f * sm * r.transform.lossyScale.x;
                        L(string.Format(CultureInfo.InvariantCulture, "외곽선 예상 폭 {0} ({1}): 폭 {2} → 오브젝트 원점-카메라 {3:F2}m, smoothstep {4:F3}, 월드 {5:F1}mm ≈ 최종 700px 그림에서 {6:F1}px (머리 중심 깊이 {7:F0}px/m)",
                            r.name, mat.name, F(wdt), d, sm, worldW * 1000f, worldW * pxPerM, pxPerM));
                    }
                }
                for (int k = 0; k < 4; k++)
                {
                    var go = gos[k];
                    foreach (var g in gos) g.SetActive(g == go);
                    float baseThick = 1f;
                    if (feat != null)
                    {
                        if (k == 3) { trig.ApplyOutline(); baseThick = trig.outlineThickness; }
                        else feat.SetActive(false);
                    }
                    go.SetActive(false); go.SetActive(true); if (k == 3) trig.Apply();
                    if (feat != null && k < 3) feat.SetActive(false);   // 툰 프리팹을 껐다 켜면 리그 OnEnable 이 피처를 다시 켠다 (k=2 는 리그를 꺼 두었지만 확인 겸)
                    if (feat != null && k == 3) feat.settings.thickness = baseThick * 2f;
                    FBX2NPRCompare.SetSS(2);
                    FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, files[k]), Vector3.forward, false, 1.18f);
                    if (feat != null && k == 3) feat.settings.thickness = baseThick;
                    string extra = "";
                    if (k == 3) { var ld = trig.LightDirection(); extra = ", 리그 빛 방향 " + V3(ld) + " (라이트와 각도 " + F(Vector3.Angle(ld, -sun.transform.forward)) + "°)"; }
                    if (k == 1 || k == 2) extra = ", 전역 _LightColor0 " + V3((Vector3)Shader.GetGlobalVector("_LightColor0")) + " _MainLightColor " + V3((Vector3)Shader.GetGlobalVector("_MainLightColor")) + " _MainLightPosition " + V3((Vector3)Shader.GetGlobalVector("_MainLightPosition"));
                    L("찍음 " + files[k] + ": 외곽선 피처 " + (feat != null && feat.isActive) + (feat != null ? " 두께(찍을 때) " + F(k == 3 ? baseThick * 2f : feat.settings.thickness) : "") + extra);
                }

                // ---- (2)와 (3)의 차이 화소 (노멀 정리가 UTS 에서 바꾼 곳)
                DiffImage(files[1], files[2], "unitychan_diff_2v3.png", "UTS 원본 노멀 vs UTS FBX2NPR 노멀");
                DiffImage(files[2], files[3], "unitychan_diff_3v4.png", "UTS FBX2NPR 노멀 vs FBX2NPR 툰");

                // ---- 보조 촬영: (a) UTS 외곽선 패스를 끈 (2)(3) — 명암만 (b) 홍채 메시를 끈 (2)(3) — 흰자 자리에 무엇이 그려지는지 (c) (2) 다시 찍기 — 찍는 순서 영향
                var utsMats = new List<Material>(utsOf.Values);
                var passWas = new List<bool>(); foreach (var m in utsMats) passWas.Add(m.GetShaderPassEnabled("SRPDefaultUnlit"));
                Action<int, string> shootUts = (k, file) =>
                {
                    foreach (var g in gos) g.SetActive(g == gos[k]);
                    gos[k].SetActive(false); gos[k].SetActive(true);
                    if (feat != null) feat.SetActive(false);
                    FBX2NPRCompare.SetSS(2);
                    FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, file), Vector3.forward, false, 1.18f);
                    L("보조 찍음 " + file + ": 외곽선 피처 " + (feat != null && feat.isActive));
                };
                foreach (var m in utsMats) m.SetShaderPassEnabled("SRPDefaultUnlit", false);
                shootUts(1, "unitychan_2_uts_orignormal_nooutline.png");
                shootUts(2, "unitychan_3_uts_toonkitnormal_nooutline.png");
                for (int i = 0; i < utsMats.Count; i++) utsMats[i].SetShaderPassEnabled("SRPDefaultUnlit", passWas[i]);
                DiffImage("unitychan_2_uts_orignormal_nooutline.png", "unitychan_3_uts_toonkitnormal_nooutline.png", "unitychan_diff_2v3_nooutline.png", "외곽선 없이: UTS 원본 노멀 vs UTS FBX2NPR 노멀");
                DiffImage(files[1], "unitychan_2_uts_orignormal_nooutline.png", "unitychan_diff_2_outline.png", "UTS 원본 노멀: 외곽선 있음 vs 없음");
                DiffImage(files[2], "unitychan_3_uts_toonkitnormal_nooutline.png", "unitychan_diff_3_outline.png", "UTS FBX2NPR 노멀: 외곽선 있음 vs 없음");
                var irises = new List<Renderer>();
                foreach (var k in new[] { 1, 2 }) foreach (var r in gos[k].GetComponentsInChildren<Renderer>(true)) if (r.name == "eye_L_old" || r.name == "eye_R_old") irises.Add(r);
                foreach (var r in irises) r.enabled = false;
                shootUts(1, "unitychan_2_uts_orignormal_noiris.png");
                shootUts(2, "unitychan_3_uts_toonkitnormal_noiris.png");
                foreach (var r in irises) r.enabled = true;
                shootUts(1, "unitychan_2_uts_orignormal_again.png");
                DiffImage(files[1], "unitychan_2_uts_orignormal_again.png", "unitychan_diff_2_again.png", "UTS 원본 노멀: 처음 vs 맨 끝에 다시");

                foreach (var g in gos) UnityEngine.Object.DestroyImmediate(g);
            }
            catch (Exception ex) { L("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                FBX2NPRCompare.SetSS(1);
                foreach (var t in temps) if (t != null) UnityEngine.Object.DestroyImmediate(t);
                File.WriteAllText(Path.Combine(OutDir, "log_uts_compare.txt"), string.Join("\n", Log.ToArray()), new UTF8Encoding(true));
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        // FBX 메시와 툰 프리팹 메시의 노멀 차이 (정점 순서가 같을 때만)
        static string MeshCompare(Mesh a, Mesh b)
        {
            if (a == null || b == null) return "메시 비교 불가";
            if (a == b) return "같은 메시 에셋 " + a.name;
            if (a.vertexCount != b.vertexCount) return "정점 수 다름 " + a.vertexCount + " vs " + b.vertexCount + " (" + b.name + ")";
            var pa = a.vertices; var pb = b.vertices; var na = a.normals; var nb = b.normals;
            double dpos = 0, ang = 0, angFace = 0; int over20 = 0, cntFace = 0, over20Face = 0;
            var fw = new List<Vector2>(); b.GetUVs(2, fw); bool hasFw = fw.Count == b.vertexCount;
            for (int i = 0; i < pa.Length; i++)
            {
                dpos += (pa[i] - pb[i]).magnitude;
                float d = Vector3.Angle(na[i], nb[i]); ang += d; if (d > 20f) over20++;
                if (hasFw && fw[i].x > 0.5f) { angFace += d; cntFace++; if (d > 20f) over20Face++; }
            }
            int n = Math.Max(1, pa.Length);
            return string.Format(CultureInfo.InvariantCulture, "메시 {0} v{1}: 위치 차 평균 {2:E1}, 노멀 각도 차 평균 {3:F1}° (20° 넘음 {4}){5}",
                b.name, b.vertexCount, dpos / n, ang / n, over20, hasFw ? string.Format(CultureInfo.InvariantCulture, ", 얼굴 가중치 정점 {0}개 평균 {1:F1}° (20° 넘음 {2})", cntFace, angFace / Math.Max(1, cntFace), over20Face) : ", 얼굴 가중치(UV2) 없음");
        }

        static Color32[] LoadPx(string name, out int w, out int h)
        {
            var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(Path.Combine(OutDir, name)));
            w = t.width; h = t.height; var px = t.GetPixels32(); UnityEngine.Object.DestroyImmediate(t); return px;
        }

        // 두 칸의 차이: 배경이 아닌 화소 중 색 차이(RGB 절대차 합 > 24)가 난 화소를 빨강으로, 나머지는 흐린 회색으로
        static void DiffImage(string fa, string fb, string outName, string label)
        {
            int w, h, w2, h2; var a = LoadPx(fa, out w, out h); var b = LoadPx(fb, out w2, out h2);
            if (w != w2 || h != h2) { L("차이 그림: 크기 다름"); return; }
            var bg = new Color32(51, 51, 56, 255);
            Func<Color32, bool> isBg = c => Mathf.Abs(c.r - bg.r) + Mathf.Abs(c.g - bg.g) + Mathf.Abs(c.b - bg.b) < 12;
            var o = new Color32[a.Length]; int fg = 0, changed = 0;
            for (int i = 0; i < a.Length; i++)
            {
                bool bgBoth = isBg(a[i]) && isBg(b[i]);
                int d = Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
                byte gy = (byte)((a[i].r + a[i].g + a[i].b) / 3);
                if (!bgBoth) fg++;
                if (!bgBoth && d > 24) { changed++; o[i] = new Color32(255, 40, 40, 255); }
                else o[i] = new Color32((byte)(gy / 3 + 20), (byte)(gy / 3 + 20), (byte)(gy / 3 + 20), 255);
            }
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false); t.SetPixels32(o); t.Apply();
            File.WriteAllBytes(Path.Combine(OutDir, outName), t.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(t);
            L(string.Format(CultureInfo.InvariantCulture, "차이 {0} ({1}): 캐릭터 화소 {2}, 색이 바뀐 화소 {3} ({4:F1}%)", outName, label, fg, changed, 100.0 * changed / Math.Max(1, fg)));
        }

        // UTS 가 무대의 디렉셔널 라이트를 받는지: 흰 베이스·검은 그림자·Step 0.5(=N·L 0)·시스템 그림자 끔 재질의 구를 찍어
        // 화소마다 구의 노멀로 계산한 N·L 부호와 대조한다. 좌우를 뒤집은 빛과도 대조해 시험이 방향을 구별하는지 확인한다.
        static void LightProbe(Light sun, List<UnityEngine.Object> temps)
        {
            var sph = GameObject.CreatePrimitive(PrimitiveType.Sphere); sph.name = "UtsLightProbe";
            UnityEngine.Object.DestroyImmediate(sph.GetComponent<Collider>());
            sph.transform.position = new Vector3(5f, 1f, 0f); sph.transform.localScale = Vector3.one * 0.3f;
            var m = new Material(Shader.Find("Toon/Toon")) { name = "UTS_probe" }; temps.Add(m);
            m.SetTexture("_MainTex", Texture2D.whiteTexture); m.SetTexture("_BaseMap", Texture2D.whiteTexture); m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Use_BaseAs1st", 1f); m.SetFloat("_Use_1stAs2nd", 1f); m.SetColor("_1st_ShadeColor", Color.black); m.SetColor("_2nd_ShadeColor", Color.black);
            m.SetFloat("_BaseColor_Step", 0.5f); m.SetFloat("_BaseShade_Feather", 0.0001f); m.SetFloat("_ShadeColor_Step", 0f);
            m.SetFloat("_Set_SystemShadowsToBase", 0f); m.SetFloat("_RimLight", 0f); m.SetFloat("_HighColor_Power", 0f); m.SetColor("_HighColor", Color.black); m.SetFloat("_GI_Intensity", 0f);
            m.EnableKeyword("_IS_CLIPPING_OFF"); m.EnableKeyword("_EMISSIVE_SIMPLE"); m.EnableKeyword("_OUTLINE_NML"); m.EnableKeyword("_IS_OUTLINE_CLIPPING_NO");
            m.SetFloat("_Outline_Width", 0f); m.SetShaderPassEnabled("SRPDefaultUnlit", false);
            var rend = sph.GetComponent<Renderer>(); rend.sharedMaterial = m; rend.shadowCastingMode = ShadowCastingMode.Off;
            var feat = FBX2NPRConverter.FindFeature(); if (feat != null) feat.SetActive(false);
            var b = new Bounds(sph.transform.position, Vector3.one * 0.3f);
            const int W = 300, H = 300; const float margin = 1.18f;
            FBX2NPRCompare.SetSS(1);
            string file = "probe_uts_sphere.png";
            FBX2NPRCompare.Shoot(b, W, H, Path.Combine(OutDir, file), Vector3.forward, false, margin);
            // FBX2NPRCompare.Shoot 과 같은 식으로 카메라를 다시 세운다
            float aspect = (float)W / H;
            float halfH = Mathf.Max(b.size.y * 0.5f, b.size.x * 0.5f / aspect);
            float dist = halfH / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * margin + b.size.z * 0.5f;
            var camPos = b.center + Vector3.forward * dist; var rot = Quaternion.LookRotation(b.center - camPos, Vector3.up);
            int w, h; var px = LoadPx(file, out w, out h);
            Vector3 Ls = -sun.transform.forward, Lm = new Vector3(-Ls.x, Ls.y, Ls.z);
            float tanH = Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad), rad = 0.15f; Vector3 c = sph.transform.position;
            int n = 0, agree = 0, agreeMirror = 0, litN = 0; double litSum = 0, shadeSum = 0; int shadeN = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                var dir = (rot * new Vector3(((x + 0.5f) / w * 2f - 1f) * tanH * aspect, ((y + 0.5f) / h * 2f - 1f) * tanH, 1f)).normalized;
                var oc = camPos - c; float bq = Vector3.Dot(oc, dir), cq = Vector3.Dot(oc, oc) - rad * rad, disc = bq * bq - cq;
                if (disc < 0f) continue;
                var p = camPos + dir * (-bq - Mathf.Sqrt(disc)); var nrm = (p - c) / rad;
                float nl = Vector3.Dot(nrm, Ls); if (Mathf.Abs(nl) < 0.1f) continue;
                bool lit = px[y * w + x].r > 128; n++;
                if (lit == (nl > 0f)) agree++;
                if (lit == (Vector3.Dot(nrm, Lm) > 0f)) agreeMirror++;
                if (nl > 0.3f) { litSum += px[y * w + x].r; litN++; }
                if (nl < -0.3f) { shadeSum += px[y * w + x].r; shadeN++; }
            }
            L(string.Format(CultureInfo.InvariantCulture, "빛 확인(UTS 구, {0}): 판정 화소 {1}, 이 라이트의 N·L 부호와 일치 {2:F2}%, 좌우 뒤집은 빛과 일치 {3:F2}% | 밝은 쪽 R 평균 {4:F1} (N·L>0.3), 그림자 쪽 R 평균 {5:F1}",
                file, n, 100.0 * agree / Math.Max(1, n), 100.0 * agreeMirror / Math.Max(1, n), litSum / Math.Max(1, litN), shadeSum / Math.Max(1, shadeN)));
            UnityEngine.Object.DestroyImmediate(sph);
        }
    }
}
