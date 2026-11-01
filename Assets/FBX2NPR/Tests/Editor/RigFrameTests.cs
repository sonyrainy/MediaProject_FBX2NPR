using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 플레이 중 매 프레임 갱신(ToonFaceRig.ApplyFrame)을 지킵니다.
    /// ApplyFrame 은 움직이는 값(빛, 얼굴 방향, 코·눈·얼굴 중심 위치)만 다시 넣으므로, 머리와 빛을 움직인 뒤
    /// ApplyFrame 이 남긴 블록과 바로 이어서 전체 갱신(Apply)이 남긴 블록이 모든 프로퍼티에서 같아야 합니다.
    /// 재질 칸이 하나인 얼굴, 칸이 둘인 얼굴(얼굴 칸 + 몸 칸), 몸 렌더러를 함께 봅니다.
    /// </summary>
    public class RigFrameTests
    {
        readonly List<Object> made = new List<Object>();

        [TearDown]
        public void Clean() { foreach (var o in made) if (o != null) Object.DestroyImmediate(o); made.Clear(); }

        static readonly string[] Floats = { "_FaceCurve", "_CurveStop", "_CurveSoft", "_CurveTilt", "_ChinScale", "_ChinBend", "_NoseShade", "_NoseLineFade", "_NoseStop",
                                            "_NoseMoon", "_EyeAvoid", "_RimLight", "_UrpLighting", "_FaceShadowReceive", "_ShadowReceive", "_NoseDebug", "_Stop2" };
        static readonly string[] Vectors = { "_LightDir", "_FaceUp", "_FaceForward", "_FaceCenter", "_NoseTip", "_NoseRadius", "_NoseMoonShape", "_NoseMoonParam", "_NoseMoonExtra",
                                             "_NosePolyA", "_NosePolyB", "_NosePolyParam", "_EyeA", "_EyeB", "_EyeRadius", "_Band1", "_InnerLineFade" };

        GameObject Go(string name, Transform parent = null)
        {
            var g = new GameObject(name); made.Add(g);
            if (parent != null) g.transform.SetParent(parent, false);
            return g;
        }

        Renderer NewRenderer(string name, Transform parent, int slots, bool firstIsFace)
        {
            var g = Go(name, parent);
            var mesh = new Mesh(); made.Add(mesh);
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.subMeshCount = slots;
            for (int i = 0; i < slots; i++) mesh.SetTriangles(new[] { 0, 1, 2 }, i);
            g.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = g.AddComponent<MeshRenderer>();
            var shader = Shader.Find("NormalTest/ToonBand");
            Assert.That(shader, Is.Not.Null, "ToonBand 셰이더");
            var mats = new Material[slots];
            for (int i = 0; i < slots; i++) { mats[i] = new Material(shader); made.Add(mats[i]); mats[i].SetFloat("_FaceWeightOn", (i == 0 && firstIsFace) ? 1f : 0f); }
            r.sharedMaterials = mats;
            return r;
        }

        // 테스트용 캐릭터: 머리 아래 리그·코끝·눈·얼굴 중심, 렌더러 셋 (칸 하나 얼굴, 칸 둘 얼굴, 몸)
        ToonFaceRig NewCharacter(out Transform head, out Transform light)
        {
            var root = Go("character"); root.SetActive(false);   // 다 채운 뒤 켜서 OnEnable(Apply) 이 한 번 돌게
            head = Go("head", root.transform).transform;
            var rig = Go("rig", head).AddComponent<ToonFaceRig>();
            light = Go("light").transform; light.rotation = Quaternion.Euler(30f, -140f, 0f);
            rig.lightTransform = light;
            rig.noseTip = Go("nose", head).transform; rig.noseTip.localPosition = new Vector3(0f, 0.05f, 0.1f);
            rig.eyeA = Go("eyeA", head).transform; rig.eyeA.localPosition = new Vector3(0.03f, 0.08f, 0.08f);
            rig.eyeB = Go("eyeB", head).transform; rig.eyeB.localPosition = new Vector3(-0.03f, 0.08f, 0.08f);
            rig.faceCenter = Go("center", head).transform;
            rig.avoidEyes = true; rig.noseHalfMoon = true; rig.noseFigure = ToonFaceRig.NoseFigure.Teardrop; rig.innerLineFade = new Vector2(0.002f, 0.004f);
            rig.faceRenderers.Add(NewRenderer("face", root.transform, 1, true));
            rig.faceRenderers.Add(NewRenderer("face2", root.transform, 2, true));
            rig.otherRenderers.Add(NewRenderer("body", root.transform, 1, false));
            root.SetActive(true);
            return rig;
        }

        static Dictionary<string, Vector4> Capture(ToonFaceRig rig)
        {
            var got = new Dictionary<string, Vector4>(); var b = new MaterialPropertyBlock();
            var all = new List<Renderer>(rig.faceRenderers); all.AddRange(rig.otherRenderers);
            foreach (var r in all)
            {
                int slots = r.sharedMaterials.Length;
                for (int i = 0; i < Mathf.Max(slots, 1); i++)
                {
                    if (slots <= 1) r.GetPropertyBlock(b); else r.GetPropertyBlock(b, i);
                    foreach (var p in Floats) got[r.name + "/" + i + "/" + p] = new Vector4(b.GetFloat(p), 0f, 0f, 0f);
                    foreach (var p in Vectors) got[r.name + "/" + i + "/" + p] = b.GetVector(p);
                }
            }
            return got;
        }

        static void AssertSameBlocks(Dictionary<string, Vector4> want, Dictionary<string, Vector4> got)
        {
            Assert.That(got.Count, Is.EqualTo(want.Count));
            foreach (var kv in want) Assert.That(Vector4.Distance(got[kv.Key], kv.Value), Is.LessThan(1e-6f), kv.Key);
        }

        [Test]
        public void FrameUpdateMatchesFullApply()
        {
            Transform head, light;
            var rig = NewCharacter(out head, out light);
            // 애니메이션 한 프레임처럼 머리를 돌리고 옮기고, 빛도 돌린다
            head.SetPositionAndRotation(new Vector3(0.1f, 1.5f, -0.2f), Quaternion.Euler(5f, 25f, -8f));
            light.rotation = Quaternion.Euler(20f, 160f, 0f);
            rig.ApplyFrame();
            var afterFrame = Capture(rig);
            Assert.That(afterFrame["face/0/_FaceUp"], Is.Not.EqualTo(new Vector4(0f, 1f, 0f, 0f)), "머리 방향이 실제로 바뀌었는지");
            rig.Apply();
            AssertSameBlocks(Capture(rig), afterFrame);
        }

        [Test]
        public void NothingMovedPushesNothing()
        {
            Transform head, light;
            var rig = NewCharacter(out head, out light);
            var face = rig.faceRenderers[0];
            var b = new MaterialPropertyBlock();
            face.GetPropertyBlock(b); b.SetVector("_LightDir", Vector4.zero); face.SetPropertyBlock(b);   // 표시용으로 블록 값을 바꿔 둔다
            rig.ApplyFrame();
            face.GetPropertyBlock(b);
            Assert.That(b.GetVector("_LightDir"), Is.EqualTo(Vector4.zero), "아무것도 안 움직였으면 블록을 다시 넣지 않는다");
        }

        [Test]
        public void RendererListChangeFallsBackToFullApply()
        {
            Transform head, light;
            var rig = NewCharacter(out head, out light);
            var added = NewRenderer("added", head.parent, 1, false);
            rig.otherRenderers.Add(added);
            rig.ApplyFrame();
            var b = new MaterialPropertyBlock(); added.GetPropertyBlock(b);
            Assert.That(b.GetVector("_LightDir").sqrMagnitude, Is.GreaterThan(0.5f), "새로 넣은 렌더러에도 값이 들어가야 한다");
        }
    }
}
