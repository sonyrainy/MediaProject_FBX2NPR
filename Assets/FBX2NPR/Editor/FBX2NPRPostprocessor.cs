using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 임포트 시점에 툰 변환을 적용합니다.
    ///
    /// DCC 왕복 대신 엔진 안에서 하는 이유는, 왕복이 조용히 망가뜨리는 것들이 있기 때문입니다.
    /// 이 프로젝트에서 실제로 겪은 것: 블렌더 Normal Edit 이 면 와인딩을 10.5% 뒤집었고
    /// (no_polynors_fix 기본값 탓), FBX 왕복에서 스케일이 100배 · 축이 Y-up 으로 바뀌었고,
    /// 좌표계 손방향이 반대로 들어와 조명 방위가 뒤집혔습니다.
    /// 임포트 시점에 처리하면 셋 다 생기지 않습니다.
    /// </summary>
    public class FBX2NPRPostprocessor : AssetPostprocessor
    {
        /// <summary>노멀을 직접 쓸 것이므로 임포터가 재계산하지 않도록 못 박습니다.</summary>
        void OnPreprocessModel()
        {
            var s = FBX2NPRAssetSettings.FromJson(assetImporter.userData);
            if (s == null || !s.enabled) return;

            var mi = (ModelImporter)assetImporter;
            // Calculate 면 파일의 커스텀 노멀을 버리고 지오메트리에서 새로 만듭니다.
            mi.importNormals = ModelImporterNormals.Import;
            // Weld 는 기본 ON 입니다. 방사형 정렬로 노멀이 같아진 정점들이 병합되면
            // 정점 수가 달라져 검증에서 인덱스 대조가 불가능해집니다.
            mi.weldVertices = false;
            mi.meshOptimizationFlags = 0;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.importBlendShapes = false;
        }

        void OnPostprocessModel(GameObject root)
        {
            var s = FBX2NPRAssetSettings.FromJson(assetImporter.userData);
            if (s == null || !s.enabled) return;

            var applied = new List<string>();
            var skipped = new List<string>();
            var jobs = new List<Job>();

            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                // 머리 본 가중치는 머리에 붙은 모든 것을 잡습니다. 재질명·메시명으로 얼굴만 남깁니다.
                if (!s.IsFaceMesh(smr.sharedMesh.name, smr.sharedMaterials)) { skipped.Add(smr.sharedMesh.name + "(비얼굴)"); continue; }
                var j = Weigh(smr.sharedMesh, smr.bones, s);
                if (j == null) { skipped.Add(smr.sharedMesh.name); continue; }
                j.l2w = smr.transform.localToWorldMatrix;   // 바인드 포즈 기준. 정점은 SMR 트랜스폼 공간에 있습니다.
                jobs.Add(j);
            }
            // 스킨이 없는 정적 메시. 유니티짱은 얼굴 전체(MTH_DEF·EYE_DEF·head_back)가 머리 본 아래 정적 메시입니다.
            // 본 가중치가 없으므로 조상 트랜스폼에 머리 본 이름이 있으면 정점 전체를 대상으로 봅니다.
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (!s.IsFaceMesh(mf.sharedMesh.name, mr != null ? mr.sharedMaterials : null)) { skipped.Add(mf.sharedMesh.name + "(정적·비얼굴)"); continue; }
                bool underHead = false;
                for (var t = mf.transform; t != null && t != root.transform; t = t.parent)
                    if (t.name.ToLowerInvariant().Contains(s.headBoneContains.ToLowerInvariant())) { underHead = true; break; }
                if (!underHead) { skipped.Add(mf.sharedMesh.name + "(정적·머리 밖)"); continue; }
                var j = new Job { mesh = mf.sharedMesh, weights = new float[mf.sharedMesh.vertexCount], subset = new List<int>(), l2w = mf.transform.localToWorldMatrix };
                for (int i = 0; i < j.weights.Length; i++) { j.weights[i] = 1f; j.subset.Add(i); }
                jobs.Add(j);
            }
            if (jobs.Count == 0)
            {
                Debug.Log("[FBX2NPR] " + System.IO.Path.GetFileName(assetPath) + "  대상 없음. 건너뜀: " + string.Join(", ", skipped.ToArray()));
                return;
            }

            // 중심은 얼굴 파트 전체를 월드로 모아 한 번만 구합니다. 파트마다 따로 구하면
            // 여러 메시로 갈라진 얼굴(유니티짱)에서 메시 경계마다 방사 방향이 달라져 이음새가 생깁니다.
            var world = new List<Vector3>();
            foreach (var j in jobs) { var v = j.mesh.vertices; foreach (var i in j.subset) world.Add(j.l2w.MultiplyPoint3x4(v[i])); }
            Vector3 centerW = RadialNormals.Centroid(world.ToArray());
            foreach (var j in jobs)
            {
                var normals = j.mesh.normals; var verts = j.mesh.vertices;
                if (normals == null || normals.Length != verts.Length) { skipped.Add(j.mesh.name + "(노멀 없음)"); continue; }
                Vector3 centerL = j.l2w.inverse.MultiplyPoint3x4(centerW);
                j.mesh.normals = RadialNormals.Apply(verts, normals, centerL, s.mix, j.weights);
                j.mesh.RecalculateTangents();
                applied.Add(j.mesh.name);
            }

            Debug.Log(string.Format(
                "[FBX2NPR] {0}  mix {1:F2} / 맵강도 {2:F2}  중심(월드) {3}\n  적용: {4}\n  건너뜀: {5}",
                System.IO.Path.GetFileName(assetPath), s.mix, s.normalMapStrength, centerW,
                applied.Count > 0 ? string.Join(", ", applied.ToArray()) : "없음",
                skipped.Count > 0 ? string.Join(", ", skipped.ToArray()) : "없음"));
        }

        class Job { public Mesh mesh; public float[] weights; public List<int> subset; public Matrix4x4 l2w; }

        /// <summary>대상 본에 실린 가중치를 정점별로 뽑습니다. 대상 정점이 없으면 null.</summary>
        static Job Weigh(Mesh mesh, Transform[] bones, FBX2NPRAssetSettings s)
        {
            if (mesh == null || bones == null || bones.Length == 0) return null;
            var targets = new HashSet<int>();
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                if (bones[i].name.ToLowerInvariant().Contains(s.headBoneContains.ToLowerInvariant())) targets.Add(i);
            }
            if (targets.Count == 0) return null;
            var bw = mesh.boneWeights;
            if (bw == null || bw.Length != mesh.vertexCount) return null;

            var j = new Job { mesh = mesh, weights = new float[mesh.vertexCount], subset = new List<int>() };
            for (int i = 0; i < bw.Length; i++)
            {
                float w = 0f;
                if (targets.Contains(bw[i].boneIndex0)) w += bw[i].weight0;
                if (targets.Contains(bw[i].boneIndex1)) w += bw[i].weight1;
                if (targets.Contains(bw[i].boneIndex2)) w += bw[i].weight2;
                if (targets.Contains(bw[i].boneIndex3)) w += bw[i].weight3;
                if (w < s.weightThreshold) { j.weights[i] = 0f; continue; }
                j.subset.Add(i);
                // 임계값에서 딱 자르면 경계에 가로줄이 생깁니다. 임계값~1 구간을 부드럽게 올립니다.
                j.weights[i] = s.smoothFalloff ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(s.weightThreshold, 1f, w)) : 1f;
            }
            return j.subset.Count > 0 ? j : null;
        }
    }
}
