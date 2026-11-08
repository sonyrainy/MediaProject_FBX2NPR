using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 원본 모델을 찍기 전에, 임포트 설정 탓에 깨져 보이는 재질만 사본에서 바로잡는다 (에셋은 건드리지 않는다).
    /// 변환기는 얼굴 파싱용 렌더 전에, 측정·캡처 도구는 원본 쪽 비교 렌더 전에 쓴다.
    ///   - 노멀맵이 일반 텍스처로 임포트된 경우: NormalMap 타입 사본을 만들어 물린다
    ///   - 알파가 있는 텍스처(머리카락·속눈썹 카드): 알파 컷아웃과 양면
    ///   - FBX 의 텍스처 연결이 끊긴 재질(유니티짱): 변환 도구와 같은 규칙으로 찾은 텍스처를 물린다
    ///   - 변환 도구가 빼는 데칼 메시: 원본에서도 끈다
    ///   - 광택 맵(_Diffuse → _Glossiness / _Specular)이 따로 있는데 연결되지 않은 경우: 합쳐서 스페큘러 워크플로로 물린다
    /// </summary>
    public static class OriginalMaterialFix
    {
        const string FixDir = FBX2NPRConverter.OutRoot + "/_CompareOriginalFix";

        /// <summary>
        /// 원본 쪽 보정. 임포트 설정 탓에 깨져 보이는 것만 재질 사본에서 바로잡는다. 그대로 두면 원본이 실제보다 나쁘게 보여 비교가 불공정하다.
        /// </summary>
        public static List<string> Apply(GameObject pbr, FBX2NPRAssetSettings crit, string modelPath, List<UnityEngine.Object> temps)
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
    }
}
