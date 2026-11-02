using System.IO;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 다시 변환해도 GUID 가 그대로라서 씬·프리팹 참조가 끊기지 않는다는 성질을 지킵니다.
    /// 변환기는 메시·재질을 SaveOrReplace 로, 구운 텍스처를 SaveTexture 로 저장하므로, 둘이 같은 경로에 덮어쓸 때 식별자를 유지하는지 봅니다.
    /// 임시 폴더에서만 만들고 지우므로 실제 변환 결과물은 건드리지 않습니다.
    /// </summary>
    public class ConverterSaveTests
    {
        const string Dir = "Assets/FBX2NPRTestTemp";

        [SetUp]
        public void SetUp() { FBX2NPRConverter.EnsureFolder(Dir); }

        [TearDown]
        public void TearDown() { AssetDatabase.DeleteAsset(Dir); }

        static Mesh MeshWith(int vertexCount)
        {
            var v = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++) v[i] = new Vector3(i, i % 2, 0f);
            return new Mesh { vertices = v, triangles = new[] { 0, 1, 2 } };
        }

        static byte[] Png(Color c)
        {
            var t = new Texture2D(4, 4);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = c;
            t.SetPixels(px); t.Apply();
            byte[] png = t.EncodeToPNG();
            Object.DestroyImmediate(t);
            return png;
        }

        [Test]
        public void SaveOrReplaceKeepsGuidAndReplacesContent()
        {
            string path = Dir + "/Part.asset";
            var first = FBX2NPRConverter.SaveOrReplace(MeshWith(3), path);
            string guid = AssetDatabase.AssetPathToGUID(path);

            var second = FBX2NPRConverter.SaveOrReplace(MeshWith(5), path);
            // 메시는 덮어쓴 뒤 다시 불러오므로(SkinnedMeshRenderer 가 예전 정점 배치를 거부한다) 객체는 바뀔 수 있다. 같은 경로·같은 GUID 의 에셋이면 된다.
            Assert.That(second, Is.SameAs(AssetDatabase.LoadAssetAtPath<Mesh>(path)), "그 경로의 에셋을 돌려줘야 합니다.");
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(second.vertexCount, Is.EqualTo(5), "내용은 새 메시로 바뀌어야 합니다.");
        }

        [Test]
        public void SaveTextureKeepsGuidWhenOverwritten()
        {
            string path = Dir + "/Skin_flat.png";
            var like = new Texture2D(4, 4);
            try
            {
                FBX2NPRConverter.SaveTexture(Png(Color.red), path, like);
                string guid = AssetDatabase.AssetPathToGUID(path);

                byte[] blue = Png(Color.blue);
                Assert.That(FBX2NPRConverter.SaveTexture(blue, path, like), Is.Not.Null);
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(File.ReadAllBytes(Path.Combine(Directory.GetParent(Application.dataPath).FullName, path)), Is.EqualTo(blue), "파일은 새 내용이어야 합니다.");
            }
            finally { Object.DestroyImmediate(like); }
        }
    }
}
