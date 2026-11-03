using System.Collections.Generic;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 변환 결과(툰 프리팹)가 원본 노멀을 제대로 실었는지 봅니다.
    /// - 메시: UV4(탄젠트 공간)를 되돌리면 UV3(물체 공간 원본 노멀)과 같아야 하고, 탄젠트는 노멀에 직교하는 진짜 탄젠트여야 합니다.
    /// - 얼굴 렌더러: 외곽선 피처가 원본 노멀로 다시 그리도록 렌더링 레이어가 켜져 있어야 합니다.
    /// 변환 결과는 저장소에 올리지 않으므로, 없으면 건너뜁니다 (먼저 변환해야 검사됩니다).
    /// </summary>
    public class ConvertedOutputTests
    {
        static List<GameObject> ToonPrefabs()
        {
            var list = new List<GameObject>();
            if (!AssetDatabase.IsValidFolder(FBX2NPRConverter.OutRoot)) return list;
            foreach (var g in AssetDatabase.FindAssets("t:Prefab", new[] { FBX2NPRConverter.OutRoot }))
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith("_Toon.prefab")) list.Add(AssetDatabase.LoadAssetAtPath<GameObject>(p));
            }
            return list;
        }

        [Test]
        public void UV4DecodesBackToOriginalNormals()
        {
            int meshes = 0;
            foreach (var pf in ToonPrefabs())
                foreach (var smr in pf.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    var m = smr.sharedMesh;
                    if (m == null || !m.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord4)) continue;
                    var ts = new List<Vector3>(); m.GetUVs(OriginalNormals.Channel, ts);
                    var os = new List<Vector3>(); m.GetUVs(3, os);
                    var n = m.normals; var t = m.tangents;
                    Assert.That(t.Length, Is.EqualTo(n.Length), pf.name + "/" + m.name + ": 탄젠트가 없습니다.");
                    float worst = 0f; int bad = 0;
                    for (int i = 0; i < n.Length; i++)
                    {
                        worst = Mathf.Max(worst, Vector3.Angle(os[i], OriginalNormals.Decode(ts[i], n[i], t[i])));
                        Vector3 tv = t[i];
                        if (tv.sqrMagnitude < 0.25f || Mathf.Abs(Vector3.Dot(n[i].normalized, tv.normalized)) > 0.01f) bad++;
                    }
                    Assert.That(worst, Is.LessThan(0.05f), pf.name + "/" + m.name + ": UV4 를 되돌린 원본 노멀이 UV3 과 다릅니다 (최대 각도).");
                    // 변환 도구는 노멀에 직교하지 않는 탄젠트(UV 가 한 점에 뭉친 곳에 임포터가 남긴 기본값 등)를 고쳐 넣는다 (OriginalNormals.Write).
                    // 예전 방식(탄젠트 자리에 원본 노멀)이면 거의 모든 정점에서 걸린다.
                    Assert.That(bad, Is.EqualTo(0), pf.name + "/" + m.name + ": 탄젠트가 없거나 노멀에 직교하지 않는 정점이 있습니다 (탄젠트를 고치기 전에 변환한 결과면 다시 변환, 아니면 예전처럼 원본 노멀이 들어 있는지 확인).");
                    meshes++;
                }
            if (meshes == 0) Assert.Ignore("원본 노멀을 UV4 에 실은 변환 결과가 없습니다 (" + FBX2NPRConverter.OutRoot + ", 다시 변환 필요)");
        }

        [Test]
        public void FaceRenderersAreMarkedForOriginalNormalLines()
        {
            int rigs = 0;
            foreach (var pf in ToonPrefabs())
            {
                var rig = pf.GetComponentInChildren<ToonFaceRig>(true);
                if (rig == null || rig.faceRenderers.Count == 0) continue;
                rigs++;
                foreach (var r in rig.faceRenderers)
                {
                    if (r == null) continue;
                    // 얼굴은 모두 외곽선 피처가 다시 그린다. 안쪽 선을 쓰는 얼굴은 원본 노멀로, 이미 평평하게 칠한 얼굴(_OutlineFlatNormal)은
                    // 카메라 방향 노멀로 그려져 외곽선이 그 얼굴 안에서는 깊이가 끊긴 곳만 선으로 본다 (2026-11-03 전에는 실루엣만 얼굴을 빼 두었다).
                    bool marked = (r.renderingLayerMask & OriginalNormals.LineLayer) != 0;
                    Assert.That(marked, Is.True, pf.name + "/" + r.name + ": 얼굴인데 외곽선 렌더링 레이어가 꺼져 있습니다 (다시 변환 필요).");
                }
            }
            if (rigs == 0) Assert.Ignore("얼굴 리그가 있는 변환 결과가 없습니다 (" + FBX2NPRConverter.OutRoot + ")");
        }

        [Test]
        public void RigValuesComeFromModelSettings()
        {
            int rigs = 0;
            foreach (var pf in ToonPrefabs())
            {
                var rig = pf.GetComponentInChildren<ToonFaceRig>(true);
                if (rig == null || string.IsNullOrEmpty(rig.sourceModelGuid)) continue;
                var imp = AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(rig.sourceModelGuid));
                if (imp == null) continue;
                var s = FBX2NPRAssetSettings.FromJson(imp.userData) ?? new FBX2NPRAssetSettings();
                // 변환 도구는 리그 인스펙터에서 저장한 조정값이 있으면 그 값을, 없으면 모델 설정의 코 둘레 선 지우기 값을 리그에 넣는다.
                // 리그에서만 바꾸면 다시 변환할 때 사라지므로, 다르면 리그 인스펙터의 '지금 값을 저장'을 누르거나 다시 변환한다.
                if (s.toonRigSaved)
                    Assert.That(JsonUtility.ToJson(ToonRigValues.From(rig)), Is.EqualTo(JsonUtility.ToJson(s.toonRig)), pf.name + ": 리그 값이 모델 설정에 저장한 조정값과 다릅니다.");
                else if (rig.noseTip != null)
                    Assert.That(rig.noseLineFade, Is.EqualTo(s.toonNoseLineFade).Within(1e-4f), pf.name + ": 리그의 코 둘레 선 지우기 값이 모델 설정과 다릅니다.");
                else continue;
                rigs++;
            }
            if (rigs == 0) Assert.Ignore("모델 설정과 비교할 얼굴 리그가 있는 변환 결과가 없습니다 (" + FBX2NPRConverter.OutRoot + ")");
        }
    }
}
