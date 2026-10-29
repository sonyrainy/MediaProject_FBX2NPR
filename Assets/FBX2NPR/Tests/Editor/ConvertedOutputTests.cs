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
                    float worst = 0f; int parallel = 0;
                    for (int i = 0; i < n.Length; i++)
                    {
                        worst = Mathf.Max(worst, Vector3.Angle(os[i], OriginalNormals.Decode(ts[i], n[i], t[i])));
                        if (Mathf.Abs(Vector3.Dot(n[i].normalized, ((Vector3)t[i]).normalized)) > 0.9f) parallel++;
                    }
                    Assert.That(worst, Is.LessThan(0.05f), pf.name + "/" + m.name + ": UV4 를 되돌린 원본 노멀이 UV3 과 다릅니다 (최대 각도).");
                    // 예전 방식(탄젠트 자리에 원본 노멀)이면 정리하지 않은 정점 거의 전부에서 탄젠트가 노멀과 평행하다.
                    // 임포트한 탄젠트도 UV 가 찌그러진 몇 정점에서는 평행할 수 있어 비율로 본다.
                    Assert.That(parallel, Is.LessThan(n.Length * 0.01f), pf.name + "/" + m.name + ": 탄젠트가 노멀과 평행한 정점이 너무 많습니다 (예전처럼 원본 노멀이 들어 있는지 확인).");
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
                    // 이미 평평하게 칠한 얼굴은 재질이 실루엣 선만 쓰므로(_OutlineFlatNormal) 원본 노멀로 다시 그리지 않는다.
                    bool innerLines = false;
                    foreach (var mat in r.sharedMaterials)
                        if (mat != null && mat.HasProperty("_OutlineFlatNormal") && mat.GetFloat("_OutlineFlatNormal") < 0.5f) innerLines = true;
                    bool marked = (r.renderingLayerMask & OriginalNormals.LineLayer) != 0;
                    Assert.That(marked, Is.EqualTo(innerLines), pf.name + "/" + r.name + (innerLines ? ": 안쪽 선을 쓰는 얼굴인데 원본 노멀 렌더링 레이어가 꺼져 있습니다." : ": 실루엣 선만 쓰는 얼굴인데 원본 노멀 렌더링 레이어가 켜져 있습니다."));
                }
            }
            if (rigs == 0) Assert.Ignore("얼굴 리그가 있는 변환 결과가 없습니다 (" + FBX2NPRConverter.OutRoot + ")");
        }
    }
}
