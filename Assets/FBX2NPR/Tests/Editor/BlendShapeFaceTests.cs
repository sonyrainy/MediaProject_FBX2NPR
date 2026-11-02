using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 표정(블렌드셰이프)이 있는 얼굴과 얼굴 뼈를 지킵니다.
    /// - 본에 묶이지 않고 표정만 있는 스킨 메시(표정을 켠 유니티짱 얼굴)는 정적 메시처럼 머리 아래 정점 전체가 얼굴 대상이어야 합니다.
    /// - 머리 뼈 아래의 입 뼈에 묶인 정점도 얼굴이어야 하고, 같은 자리의 머리카락 뼈는 아니어야 합니다.
    /// - 표정은 얼굴 정점에서 모양만 바꾸고(위치 변화량 유지) 노멀·탄젠트 변화량은 0 이어야 합니다. 얼굴 밖 정점은 그대로여야 합니다.
    /// </summary>
    public class BlendShapeFaceTests
    {
        static Mesh Quad()
        {
            var m = new Mesh { name = "Face" };
            m.vertices = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0) };
            m.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            m.RecalculateNormals(); m.RecalculateTangents();
            var dv = new Vector3[4]; var dn = new Vector3[4]; var dt = new Vector3[4];
            for (int i = 0; i < 4; i++) { dv[i] = new Vector3(0, 0, 0.1f * (i + 1)); dn[i] = new Vector3(0.2f, 0, 0); dt[i] = new Vector3(0, 0.3f, 0); }
            m.AddBlendShapeFrame("Smile", 100f, dv, dn, dt);
            return m;
        }

        [Test]
        public void BonelessSkinnedMeshUnderHeadIsWeighedAsStatic()
        {
            var root = new GameObject("model"); var mesh = Quad();
            try
            {
                var head = new GameObject("Character1_Head"); head.transform.SetParent(root.transform, false);
                var go = new GameObject("MTH"); go.transform.SetParent(head.transform, false);
                var smr = go.AddComponent<SkinnedMeshRenderer>(); smr.sharedMesh = mesh;
                var crit = new FBX2NPRAssetSettings { headBoneContains = "head" };
                Assert.That(FaceMeshRule.HasSkin(smr, mesh), Is.False);
                var w = FaceMeshRule.WeighSkinned(smr, mesh, root.transform, crit);
                Assert.That(w.headCount, Is.EqualTo(4), "머리 아래의 본 없는 스킨 메시가 얼굴 대상에서 빠졌습니다");
                foreach (var x in w.head) Assert.That(x, Is.EqualTo(1f));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        // 얼굴 메시에서 머리 뼈 아래 입 뼈는 얼굴로 세고, 같은 자리의 머리카락 뼈는 세지 않는다. 프레이밍용 head 는 머리 뼈만
        [Test]
        public void FaceMeshCountsMouthBoneUnderHeadButNotHairBone()
        {
            var root = new GameObject("model"); var mesh = Quad();
            try
            {
                var head = new GameObject("Bip001 Head").transform; head.SetParent(root.transform, false);
                var mouth = new GameObject("Bdy_M_Mouth").transform; mouth.SetParent(head, false);
                var hair = new GameObject("Hair_01").transform; hair.SetParent(head, false);
                var body = new GameObject("Spine").transform; body.SetParent(root.transform, false);
                mesh.boneWeights = new[]
                {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },   // 머리
                    new BoneWeight { boneIndex0 = 1, weight0 = 1f },   // 입
                    new BoneWeight { boneIndex0 = 2, weight0 = 1f },   // 머리카락
                    new BoneWeight { boneIndex0 = 3, weight0 = 1f },   // 몸
                };
                var crit = new FBX2NPRAssetSettings { headBoneContains = "head" };
                var w = FaceMeshRule.Weigh(mesh, new[] { head, mouth, hair, body }, null, crit);
                Assert.That(w.isFace, Is.True);
                Assert.That(w.face, Is.EqualTo(new[] { 1f, 1f, 0f, 0f }), "얼굴 가중치: 머리·입은 얼굴, 머리카락·몸은 아님");
                Assert.That(w.head, Is.EqualTo(new[] { 1f, 0f, 0f, 0f }), "프레이밍용 가중치는 머리 뼈만");
                Assert.That(w.faceCount, Is.EqualTo(2));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void FreezeShapeNormalsKeepsShapeAndClearsFaceNormalDeltas()
        {
            var mesh = Quad();
            try
            {
                int changed = FBX2NPRConverter.FreezeShapeNormals(mesh, new[] { 1f, 0.5f, 0f, 0f });
                Assert.That(changed, Is.EqualTo(1));
                Assert.That(mesh.blendShapeCount, Is.EqualTo(1));
                Assert.That(mesh.GetBlendShapeName(0), Is.EqualTo("Smile"));
                Assert.That(mesh.GetBlendShapeFrameWeight(0, 0), Is.EqualTo(100f));
                var dv = new Vector3[4]; var dn = new Vector3[4]; var dt = new Vector3[4];
                mesh.GetBlendShapeFrameVertices(0, 0, dv, dn, dt);
                for (int i = 0; i < 4; i++)
                {
                    Assert.That(dv[i].z, Is.EqualTo(0.1f * (i + 1)).Within(1e-6f), "위치 변화량은 그대로여야 합니다");
                    bool face = i < 2;
                    Assert.That(dn[i], Is.EqualTo(face ? Vector3.zero : new Vector3(0.2f, 0, 0)), "정점 " + i + " 노멀 변화량");
                    Assert.That(dt[i], Is.EqualTo(face ? Vector3.zero : new Vector3(0, 0.3f, 0)), "정점 " + i + " 탄젠트 변화량");
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
