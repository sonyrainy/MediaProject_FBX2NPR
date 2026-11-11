using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 프리팹의 얼굴 SDF 맵을 텍스처마다 lilToon 마스크(프리팹 폴더의 &lt;텍스처&gt;_lilToonSDF.png)와 재질(&lt;텍스처&gt;_lilToon.mat)로 내보낸다. 계산은 FaceSdfLilToon.
    /// 재질이 이미 있으면 SDF 값만 바꾸고 색 등 나머지는 둔다. 새 재질은 얼굴 텍스처를 넣고, 우리 얼굴처럼 그림자를 한 단만 쓴다. 프리팹은 바꾸지 않는다.
    /// 재질 값은 재질 전체에 걸린다: 같은 재질을 쓰는 몸(마스크 B = 255)도 경계 0.5·얼굴 명암선에 맞춘 경계 폭·2번째 그림자 끔을 따르고,
    /// lilToon 의 SDF 모드는 재질 전체의 경계 안티에일리어싱을 끈다. 몸 경계에 계단이 보이면 _ShadowBlur 를 올리면 된다 (경계 위치는 그대로다).
    /// lilToon 은 렌더러의 물체 축(+Z 앞, −X 옆)을 얼굴 방향으로 쓴다. 리그 입력이 없어서 맵으로는 고칠 수 없고, 어긋나면 내보낼 때 알린다.
    /// </summary>
    public static class FaceSdfLilToonExport
    {
        /// <summary>마스크의 긴 변 상한. 얼굴이 큰 텍스처의 작은 부분이면(unitychan 의 skin_01) 펼친 크기가 4000 을 넘는다.</summary>
        public const int MaxSize = 2048;
        /// <summary>물체 축과 얼굴 앞이 이보다 어긋나면 알린다 (도).</summary>
        const float AxisWarnDeg = 5f;
        /// <summary>lilToon _ShadowMaskType 의 SDF (0 세기, 1 평면, 2 SDF).</summary>
        const float MaskTypeSdf = 2f;

        public static FaceSdfBake.Report Export(string prefabPath)
        {
            var rep = new FaceSdfBake.Report();
            var lil = Shader.Find("lilToon");
            if (lil == null) { rep.lines.Add("lilToon 셰이더가 없습니다. lilToon 1.9.0 이상을 설치한 뒤 다시 누르세요."); return rep; }
            string outDir = Path.GetDirectoryName(prefabPath).Replace('\\', '/');
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var rig = root.GetComponentInChildren<ToonFaceRig>(true);
                if (rig == null) { rep.lines.Add("리그 없음: " + prefabPath); return rep; }
                bool nose = rig.noseShadow && rig.noseTip != null && rig.noseStrength > 0f;
                var groups = new Dictionary<string, List<FaceSdfBake.FaceMaterialSlot>>();
                foreach (var s in FaceSdfBake.FaceSlots(rig))
                {
                    string key = FaceSdfBake.TexKey(s.material);
                    List<FaceSdfBake.FaceMaterialSlot> list;
                    if (!groups.TryGetValue(key, out list)) groups[key] = list = new List<FaceSdfBake.FaceMaterialSlot>();
                    list.Add(s);
                }
                foreach (var g in groups) ExportOne(rig, prefabPath, g.Key, g.Value, outDir, lil, nose, rep);
                if (rep.maps.Count == 0) { rep.lines.Add("내보낼 SDF 맵이 없습니다. 먼저 '굽기'를 누르세요."); return rep; }
                float lo, hi;
                FaceSdfLilToon.Window(FaceSdfLilToon.Border, out lo, out hi);
                rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "lilToon 기본 경계 {0:F1} 로 빛 {1:F0}°~{2:F0}° 를 담습니다. 그 밖의 빛에서는 명암이 FBX2NPR 과 달라집니다.", FaceSdfLilToon.Border, lo, hi));
                rep.lines.Add("그늘 경계 폭은 얼굴 명암선에 맞췄습니다. 같은 재질을 쓰는 몸도 경계가 또렷해지고 2번째 그림자가 꺼집니다 (계단이 보이면 _ShadowBlur 를 올리세요).");
                if (nose) rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "코 그림자는 큰 명암선에 합쳐 진하기 100% 로 그립니다 (FBX2NPR 은 {0:0%}). 밝은 코 초승달과 코 그림자가 옅어지는 효과는 lilToon 마스크에 담을 수 없어 뺐습니다.", rig.noseStrength));
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            AssetDatabase.SaveAssets();
            return rep;
        }

        static void ExportOne(ToonFaceRig rig, string prefabPath, string texKey, List<FaceSdfBake.FaceMaterialSlot> slots, string outDir, Shader lil, bool nose, FaceSdfBake.Report rep)
        {
            Color32[] ours; int w, h; Rect rect;
            try { if (!FaceSdfBake.ReadMap(prefabPath, texKey, out ours, out w, out h, out rect)) return; }   // 맵이 없다: 규칙으로 둔 텍스처 (얼굴 앞면의 2% 미만)
            catch (InvalidDataException e) { rep.lines.Add(texKey + ": " + e.Message); return; }
            if (nose) ours = FaceSdfLilToon.MergeNose(ours);

            var uvs = new List<Vector2>(); var wts = new List<float>(); var tris = new List<int>();
            CollectTriangles(slots, uvs, wts, tris);
            int ow, oh;
            FaceSdfLilToon.OutputSize(w, h, rect, MaxSize, out ow, out oh);
            var px = FaceSdfLilToon.Convert(ours, w, h, rect, FaceSdfLilToon.FaceWeightMap(uvs, wts, tris, ow, oh), ow, oh);

            string texPath = outDir + "/" + texKey + "_lilToonSDF.png";
            var tex = new Texture2D(ow, oh, TextureFormat.RGBA32, false, true);
            try { tex.SetPixels32(px); tex.Apply(false); File.WriteAllBytes(texPath, tex.EncodeToPNG()); }
            finally { Object.DestroyImmediate(tex); }
            // SDF 맵과 같은 가져오기 설정 (BC7, 밉맵 없음). 마스크의 압축 오차를 따로 재지는 않았고, 빛 60°~120° 에서 우리 셰이더와 다른 화소로 결과를 확인했다 (2026-11-11, 3명 0.5~4.0%).
            FaceSdfBake.ImportDataTexture(texPath);
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

            string matPath = outDir + "/" + texKey + "_lilToon.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            bool made = mat == null;
            if (made)
            {
                mat = new Material(lil);
                var alb = slots[0].material.GetTexture("_Albedo");
                if (alb != null) mat.SetTexture("_MainTex", alb);
                var c2 = mat.GetColor("_Shadow2ndColor"); c2.a = 0f; mat.SetColor("_Shadow2ndColor", c2);   // 2번째 그림자도 SDF 값을 따라 한 단 더 생긴다. 우리 얼굴은 한 단
                AssetDatabase.CreateAsset(mat, matPath);
            }
            mat.SetFloat("_UseShadow", 1f);
            mat.SetFloat("_ShadowMaskType", MaskTypeSdf);
            mat.SetTexture("_ShadowStrengthMask", mask);
            mat.SetFloat("_ShadowStrengthMaskLOD", 0f);
            mat.SetFloat("_ShadowFlatBlur", 0.001f);   // 얼굴·빛 방향의 세로 성분을 지워 수평 각만 본다 (우리 맵과 같다)
            mat.SetFloat("_ShadowBorder", FaceSdfLilToon.Border);
            // 경계 폭: 우리 명암선은 빛 각도로 2·lineSoftness(라디안) 동안 바뀌고, lilToon 은 v 가 blur 만큼 바뀌는 동안 바뀐다.
            // 경계 0.5 의 범위 가운데(90°)에서 dv/dθ = 0.5 라 blur = lineSoftness 면 폭이 같다. 같은 재질을 쓰는 몸의 그늘 경계도 그만큼 또렷해진다.
            mat.SetFloat("_ShadowBlur", rig.lineSoftness);
            EditorUtility.SetDirty(mat);
            rep.maps.Add(texPath);
            rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}: 마스크 {1}x{2} → {3}, 재질 {4} ({5})", texKey, ow, oh, texPath, matPath, made ? "새로 만듦" : "SDF 값만 바꿈"));

            // lilToon 은 렌더러의 물체 축을 얼굴 방향으로 쓴다. 스킨 메시는 루트 본의 축이다.
            // 루트 본을 얼굴 앞을 보는 빈 오브젝트로 바꾸면 맞는다 (2026-11-11 Galaxia: glTF 에서 변환한 _rootJoint 가 90° 돌아가 있어 명암이 빛을 따라 움직이지 않았고, 바꾼 뒤 맞았다)
            var seen = new HashSet<Renderer>();
            foreach (var s in slots)
            {
                var r = s.renderer; if (!seen.Add(r)) continue;
                var smr = r as SkinnedMeshRenderer;
                var t = smr != null && smr.rootBone != null ? smr.rootBone : r.transform;
                float err = Mathf.Max(FlatAngle(t.forward, rig.transform.forward), FlatAngle(t.right, rig.transform.right));
                if (err > AxisWarnDeg)
                    rep.lines.Add(string.Format(CultureInfo.InvariantCulture, "  주의: {0} 의 물체 축({1})이 얼굴 앞과 {2:F0}° 어긋나, 이 렌더러에서는 lilToon 그림자 방향이 틀립니다.{3}",
                        r.name, t.name, err, smr != null ? " 루트 본을 얼굴 앞(+Z)을 보는 빈 오브젝트로 바꾸면 맞습니다." : ""));
            }
        }

        // 얼굴 재질 칸에 속한 삼각형의 UV 와 꼭짓점 얼굴 가중치를 모은다. _FaceWeightOn 재질은 UV2.x (없으면 0, 우리 셰이더와 같다), 아니면 1.
        static void CollectTriangles(List<FaceSdfBake.FaceMaterialSlot> slots, List<Vector2> uvs, List<float> wts, List<int> tris)
        {
            var done = new HashSet<FaceSdfBake.FaceMaterialSlot>();
            foreach (var s in slots)
            {
                if (!done.Add(s)) continue;
                var r = s.renderer; var smr = r as SkinnedMeshRenderer; var mf = r.GetComponent<MeshFilter>();
                Mesh mesh = smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);
                if (mesh == null) continue;
                var uv = mesh.uv;
                if (uv.Length != mesh.vertexCount) continue;
                var fw = new List<Vector2>(); mesh.GetUVs(2, fw);
                bool useW = s.material.GetFloat("_FaceWeightOn") > 0.5f;   // FaceSlots 가 _FaceWeightOn 이 있는 재질만 준다
                int start = uvs.Count;
                for (int i = 0; i < mesh.vertexCount; i++) { uvs.Add(uv[i]); wts.Add(!useW ? 1f : fw.Count == mesh.vertexCount ? Mathf.Clamp01(fw[i].x) : 0f); }
                var mats = r.sharedMaterials;
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    if (mats[Mathf.Min(sub, mats.Length - 1)] != s.material) continue;   // 서브메시 → 재질 칸 매핑은 FaceSdfBake.CollectGroups 와 같다
                    foreach (var i in mesh.GetTriangles(sub)) tris.Add(start + i);
                }
            }
        }

        // 수평면(월드 xz)에서 두 방향 사이 각도. lilToon 이 xz 로 비교한다. 한쪽이 거의 수직이면 90°
        static float FlatAngle(Vector3 a, Vector3 b)
        {
            var p = new Vector2(a.x, a.z); var q = new Vector2(b.x, b.z);
            if (p.sqrMagnitude < 1e-4f || q.sqrMagnitude < 1e-4f) return 90f;
            return Vector2.Angle(p, q);
        }
    }
}
