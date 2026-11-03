using System.Collections.Generic;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 얼굴 메시 처리 규칙 한곳. 변환기(FBX2NPRConverter)·임포터(FBX2NPRPostprocessor)·실험(GeneralizationTest)·미리보기(ToonFacePreview)가 전부 여기를 쓴다.
    ///
    ///   1) 머리 본 가중치: 스킨 메시는 이름에 머리 본 문자열(headBoneContains)이 든 본들에 실린 가중치의 합,
    ///      정적 메시는 조상 트랜스폼 이름에 그 문자열이 있으면 정점 전체 (유니티짱은 얼굴 전체가 머리 본 아래 정적 메시).
    ///      본에 묶이지 않은 스킨 메시(표정만 있는 메시, 표정을 켠 유니티짱 얼굴)도 정적 메시로 본다 (HasSkin).
    ///      얼굴 메시로 판정된 스킨 메시는 머리 뼈 아래에 붙은 얼굴 뼈(이름 규칙 faceBoneContains: 입·턱·눈 등)의 가중치도 얼굴로 센다.
    ///      임계값 미만은 0, 임계값~1 은 SmoothStep 으로 올린다 — 임계값에서 딱 자르면 목에 가로 단절선이 생긴다 (초기 블렌더 실험).
    ///   2) 얼굴 메시 판정(FBX2NPRAssetSettings.IsFaceMesh)에 걸리지 않으면 가중치를 전부 0 으로 — 머리 본만으로는
    ///      머리카락·속눈썹·모자·이·시가까지 잡힌다 (3체 실험). 카메라 프레이밍용으로는 판정 전 가중치(head)를 따로 돌려준다.
    ///   3) 방사형 중심은 얼굴 파트 전체를 월드로 모아 한 번만 구한다 (RadialNormals.Centroid, 중복 정점 제거).
    ///      파트마다 구하면 여러 메시로 갈라진 얼굴(유니티짱)에서 메시 경계마다 방사 방향이 달라져 이음새가 생긴다.
    ///
    /// 10/22 전까지는 네 곳에 같은 코드가 있었고, 실험·미리보기는 머리 본 이름을 "head" 로 고정해 설정값과 어긋날 수 있었다.
    /// </summary>
    public static class FaceMeshRule
    {
        /// <summary>이 가중치 미만인 정점은 건드리지 않는다. 변환기·실험·미리보기 공통값 (임포터만 설정값 weightThreshold 를 쓴다).</summary>
        public const float WeightThreshold = 0.5f;

        /// <summary>메시 하나의 가중치. head 는 얼굴 판정과 무관한 머리 본 가중치(프레이밍용), face 는 얼굴 메시일 때만 값이 있고(스킨 메시는 머리 뼈 아래 얼굴 뼈까지) 아니면 전부 0.</summary>
        public class Weights
        {
            public float[] head, face;
            public int headCount;   // head 에서 임계값 이상인 정점 수
            public int faceCount;   // 얼굴 메시면 headCount, 아니면 0
            public bool isFace;
        }

        /// <summary>
        /// 스킨 메시의 머리 본 가중치. 본 이름에 headKey(대소문자 무시)가 든 본이 없거나 키가 비면 전부 0.
        /// faceBones(; 로 구분)를 주면 머리 뼈 아래에 붙은 본 중 이름에 그 문자열이 든 본(입·눈 뼈)의 가중치도 더한다.
        /// 게임용 리그는 립싱크용 입 뼈를 머리 뼈 아래에 따로 둔다 (친구 모델: 얼굴 정점 1957개 중 316개가 입·눈 뼈에 묶여 정리에서 빠지고,
        /// 입 둘레만 원래 노멀이 남아 수염처럼 칠해졌다). 머리 뼈 아래 본을 전부 세면 머리카락·귀·장신구 뼈까지 들어온다 (Pronama 앞머리 2천여 정점).
        /// count 는 임계값 이상인 정점 수 (임계값과 같으면 올린 값이 0 이어도 센다 — 예전 네 곳과 같은 규칙).
        /// </summary>
        public static float[] SkinnedHeadWeights(Mesh mesh, Transform[] bones, string headKey, float threshold, bool smooth, out int count, string faceBones = null)
        {
            var w = new float[mesh.vertexCount]; count = 0;
            string key = (headKey ?? "").ToLowerInvariant();
            if (key.Length == 0 || bones == null) return w;
            var extra = new List<string>();
            foreach (var s0 in (faceBones ?? "").ToLowerInvariant().Split(';')) if (s0.Trim().Length > 0) extra.Add(s0.Trim());
            var heads = new HashSet<int>();
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i] == null) continue;
                string nm = bones[i].name.ToLowerInvariant();
                if (nm.Contains(key) || (extra.Exists(e => nm.Contains(e)) && IsUnderHead(bones[i], null, key))) heads.Add(i);
            }
            var bw = mesh.boneWeights;
            if (heads.Count == 0 || bw == null || bw.Length != mesh.vertexCount) return w;
            for (int i = 0; i < bw.Length; i++)
            {
                float s = 0f;
                if (heads.Contains(bw[i].boneIndex0)) s += bw[i].weight0;
                if (heads.Contains(bw[i].boneIndex1)) s += bw[i].weight1;
                if (heads.Contains(bw[i].boneIndex2)) s += bw[i].weight2;
                if (heads.Contains(bw[i].boneIndex3)) s += bw[i].weight3;
                if (s < threshold) continue;
                w[i] = smooth ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(threshold, 1f, s)) : 1f;
                count++;
            }
            return w;
        }

        /// <summary>정적 메시: t 자신부터 root 직전까지 조상 이름에 headKey 가 들어 있으면 머리 아래. 키가 비면 아니다.</summary>
        public static bool IsUnderHead(Transform t, Transform root, string headKey)
        {
            string key = (headKey ?? "").ToLowerInvariant();
            if (key.Length == 0) return false;
            for (; t != null && t != root; t = t.parent)
                if (t.name.ToLowerInvariant().Contains(key)) return true;
            return false;
        }

        /// <summary>정적 메시의 가중치: 머리 아래면 정점 전체 1, 아니면 전부 0.</summary>
        public static float[] StaticHeadWeights(int vertexCount, bool underHead, out int count)
        {
            var w = new float[vertexCount]; count = 0;
            if (underHead) { for (int i = 0; i < w.Length; i++) w[i] = 1f; count = w.Length; }
            return w;
        }

        /// <summary>
        /// 본 가중치가 있는 스킨 메시인가. 유니티는 본에 묶이지 않고 표정(블렌드셰이프)만 있는 메시도 SkinnedMeshRenderer 로 가져온다
        /// (유니티짱 얼굴: 표정을 켜고 가져오면 MTH_DEF·EYE_DEF 가 본 0개인 SkinnedMeshRenderer). 이런 메시는 정적 메시 규칙으로 본다.
        /// </summary>
        public static bool HasSkin(SkinnedMeshRenderer smr, Mesh mesh)
        {
            if (smr == null || mesh == null || smr.bones.Length == 0) return false;
            var bw = mesh.boneWeights; return bw != null && bw.Length == mesh.vertexCount;
        }

        /// <summary>SkinnedMeshRenderer 한 개의 규칙 전체. 본 가중치가 없으면(표정만 있는 메시) 정적 메시 규칙을 쓴다.</summary>
        public static Weights WeighSkinned(SkinnedMeshRenderer smr, Mesh mesh, Transform root, FBX2NPRAssetSettings crit)
        {
            return HasSkin(smr, mesh) ? Weigh(mesh, smr.bones, smr.sharedMaterials, crit) : WeighStatic(mesh, smr.transform, root, smr.sharedMaterials, crit);
        }

        /// <summary>
        /// 스킨 메시 한 개의 규칙 전체: 머리 본 가중치 → 얼굴 판정. 얼굴 메시는 머리 뼈 아래 얼굴 뼈(faceBoneContains: 입·눈 등)의 가중치까지 얼굴로 본다.
        /// 프레이밍용 head 는 머리 뼈만 센다.
        /// </summary>
        public static Weights Weigh(Mesh mesh, Transform[] bones, Material[] mats, FBX2NPRAssetSettings crit)
        {
            int n; var head = SkinnedHeadWeights(mesh, bones, crit.headBoneContains, WeightThreshold, true, out n);
            var r = Gate(mesh.name, mats, crit, head, n);
            if (r.isFace) { int nf; r.face = SkinnedHeadWeights(mesh, bones, crit.headBoneContains, WeightThreshold, true, out nf, crit.faceBoneContains); r.faceCount = nf; }
            return r;
        }

        /// <summary>정적 메시 한 개의 규칙 전체: 머리 아래인가 → 얼굴 판정.</summary>
        public static Weights WeighStatic(Mesh mesh, Transform t, Transform root, Material[] mats, FBX2NPRAssetSettings crit)
        {
            int n; var head = StaticHeadWeights(mesh.vertexCount, IsUnderHead(t, root, crit.headBoneContains), out n);
            return Gate(mesh.name, mats, crit, head, n);
        }

        static Weights Gate(string meshName, Material[] mats, FBX2NPRAssetSettings crit, float[] head, int headCount)
        {
            var r = new Weights { head = head, headCount = headCount, isFace = crit.IsFaceMesh(meshName, mats) };
            r.face = r.isFace ? (float[])head.Clone() : new float[head.Length];
            r.faceCount = r.isFace ? headCount : 0;
            return r;
        }

        /// <summary>방사형 노멀을 받을 파트 하나. weights 가 0 보다 큰 정점만 중심 계산과 정리에 들어간다.</summary>
        public struct Target
        {
            public Vector3[] vertices, origN;
            public Matrix4x4 l2w;      // 메시 좌표 → 월드 (스킨 메시는 호출하는 쪽이 바인드 포즈를 반영해 넘긴다)
            public float[] weights;    // 얼굴 가중치
            public int count;          // 가중치가 있는 정점 수. 0 이면 이 파트는 뺀다
        }

        /// <summary>얼굴 파트 전체의 방사형 중심(월드). 가중치 > 0 인 정점을 월드로 모아 중복을 뺀 평균. 대상이 없으면 Vector3.zero.</summary>
        public static Vector3 Centroid(IList<Target> parts)
        {
            var world = new List<Vector3>();
            foreach (var p in parts)
            {
                if (p.count == 0) continue;
                for (int i = 0; i < p.vertices.Length; i++)
                    if (p.weights[i] > 0f) world.Add(p.l2w.MultiplyPoint3x4(p.vertices[i]));
            }
            return world.Count > 0 ? RadialNormals.Centroid(world.ToArray()) : Vector3.zero;
        }

        /// <summary>파트 하나의 노멀을 중심(월드) 기준 방사형으로 mix 만큼 정리한 새 배열.</summary>
        public static Vector3[] Apply(Target p, Vector3 centerW, float mix)
        {
            return RadialNormals.Apply(p.vertices, p.origN, p.l2w.inverse.MultiplyPoint3x4(centerW), mix, p.weights);
        }
    }
}
