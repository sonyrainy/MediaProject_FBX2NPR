using System.Collections.Generic;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 눈알이 따로 된 메시·텍스처인 모델의 파서 신뢰 판정(FBX2NPRConverter.SeparateEyeTexture)을 지킵니다.
    /// Emma 는 얼굴 텍스처에 눈이 0.19% 뿐이라 예전 방식으로 빠졌지만, 눈 메시 텍스처에서는 69.7% 가 눈이었습니다.
    /// 눈 메시만 쓰는 텍스처에서 눈이 확실할 때만 인정하고, 머리카락·속눈썹이 같이 쓰는 텍스처나 눈이 조금뿐인 텍스처는 인정하지 않아야 합니다.
    /// </summary>
    public class SeparateEyeTextureTests
    {
        readonly List<Texture2D> made = new List<Texture2D>();

        FBX2NPRParseBake.TexResult Tex(string name, int observed, int eyeTexels, params string[] meshes)
        {
            var tex = new Texture2D(2, 2) { name = name }; made.Add(tex);
            var t = new FBX2NPRParseBake.TexResult { albedo = tex, observed = observed };
            t.classCount[FaceParser.LEye] = eyeTexels;
            t.meshes.AddRange(meshes);
            return t;
        }

        [TearDown]
        public void Clean() { foreach (var t in made) Object.DestroyImmediate(t); made.Clear(); }

        [Test]
        public void FindsEyesOnASeparateEyeTexture()
        {
            var face = Tex("baseColor_001", 274000, 520, "Wolf3D_Head");
            var eye = Tex("baseColor", 1000, 697, "EyeLeft", "EyeRight");   // Emma: 69.7%
            Assert.That(FBX2NPRConverter.SeparateEyeTexture(new[] { face, eye }, face.albedo), Is.Not.Null);
        }

        [Test]
        public void IgnoresTexturesSharedWithHairOrLashes()
        {
            var face = Tex("Ch07_1001_Diffuse", 400000, 7000, "Ch07_Body");
            var hair = Tex("Ch07_1002_Diffuse", 300000, 9000, "Ch07_Eyelashes", "Ch07_Hair");
            Assert.That(FBX2NPRConverter.SeparateEyeTexture(new[] { face, hair }, face.albedo), Is.Null);
        }

        [Test]
        public void IgnoresEyeTexturesWithFewEyePixels()
        {
            var face = Tex("face", 274000, 100, "Head");
            var eye = Tex("eyes", 1000, 50, "EyeLeft", "EyeRight");   // 5%: 눈 메시라도 눈이 확실하지 않다
            Assert.That(FBX2NPRConverter.SeparateEyeTexture(new[] { face, eye }, face.albedo), Is.Null);
        }
    }
}
