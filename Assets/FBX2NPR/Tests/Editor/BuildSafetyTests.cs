using NUnit.Framework;
using UnityEditor;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 빌드에서만 드러났던 문제가 다시 생기지 않는지 에디터에서 미리 봅니다.
    /// </summary>
    public class BuildSafetyTests
    {
        // 1차 빌드에서 외곽선이 사라졌던 원인: 셰이더를 Shader.Find 로만 찾아서, 아무도 참조하지 않는 셰이더가 빌드에서 빠졌습니다.
        [Test]
        public void SobelFeatureKeepsSerializedShaderReference()
        {
            int found = 0;
            foreach (var g in AssetDatabase.FindAssets("t:UniversalRendererData"))
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null) continue;
                foreach (var f in data.rendererFeatures)
                {
                    var sobel = f as SobelOutlineFeature;
                    if (sobel == null) continue;
                    found++;
                    Assert.That(sobel.shader, Is.Not.Null, path + ": 외곽선 셰이더 참조가 비어 있으면 빌드에서 셰이더가 빠집니다.");
                    if (sobel.settings.restoreOriginalNormals)
                        Assert.That(sobel.lineNormalsShader, Is.Not.Null, path + ": 원본 노멀 패스 셰이더 참조가 비어 있으면 빌드에서 셰이더가 빠집니다.");
                }
            }
            if (found == 0) Assert.Ignore("외곽선 피처를 쓰는 렌더러가 없습니다.");
        }
    }
}
