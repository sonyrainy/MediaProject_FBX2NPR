using System;
using System.Collections.Generic;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 노멀 정리 전후 얼굴 그림자 얼룩 수가 기준선만큼 줄어드는지 봅니다.
    /// 얼룩 수 = 3단 클레이 렌더에서 얼굴 마스크 안 연결 성분 수. GeneralizationTest(메뉴 "3체 일반화 실험")를 그대로 돌려 결과를 읽습니다.
    /// 실험이 바꾸는 외곽선 피처의 값·켜짐과 셰이더 비동기 컴파일 설정은 끝나면 되돌리고, 결과 그림은 NormalTestOut/gen6_regress 에 따로 남깁니다.
    /// 렌더 결과라 GPU·드라이버에 따라 몇 개씩 달라질 수 있어, 감소율이 기준선보다 10%p 넘게 낮을 때만 실패로 봅니다.
    /// 테스트셋이 없으면 건너뜁니다. 네 캐릭터를 렌더하므로 1~2분 걸립니다.
    /// </summary>
    [Category("Slow")]
    public class BlotchRegressionTests
    {
        const string RendererPath = "Assets/Settings/PC_Renderer.asset";
        static readonly string[] Chars = { "Ch07", "TheBoss", "Jones", "unitychan" };
        Dictionary<string, Dictionary<string, int[]>> comps;

        [OneTimeSetUp]
        public void RunExperiment()
        {
            SobelOutlineFeature feat = null;
            var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
            if (rd != null) foreach (var f in rd.rendererFeatures) if (f is SobelOutlineFeature) feat = (SobelOutlineFeature)f;
            if (feat == null) return;

            bool activeWas = feat.isActive, asyncWas = ShaderUtil.allowAsyncCompilation;
            string settingsWas = JsonUtility.ToJson(feat.settings);
            string variantWas = Environment.GetEnvironmentVariable("FBX2NPR_VARIANT"), onlyWas = Environment.GetEnvironmentVariable("FBX2NPR_ONLY");
            try
            {
                // 어떤 실험 토큰과도 겹치지 않는 이름이라 동작은 기본 실행과 같고, 출력 폴더만 gen6_regress 로 갈립니다.
                Environment.SetEnvironmentVariable("FBX2NPR_VARIANT", "regress");
                Environment.SetEnvironmentVariable("FBX2NPR_ONLY", string.Join(";", Chars));
                GeneralizationTest.Run();
                comps = new Dictionary<string, Dictionary<string, int[]>>(GeneralizationTest.LastComps);
            }
            finally
            {
                Environment.SetEnvironmentVariable("FBX2NPR_VARIANT", variantWas);
                Environment.SetEnvironmentVariable("FBX2NPR_ONLY", onlyWas);
                JsonUtility.FromJsonOverwrite(settingsWas, feat.settings);
                feat.SetActive(activeWas);
                ShaderUtil.allowAsyncCompilation = asyncWas;
            }
        }

        // 기준선 감소율(%): 정면, 측면
        [TestCase("Ch07", 51, 45)]
        [TestCase("TheBoss", 69, 72)]
        [TestCase("Jones", 55, 80)]
        [TestCase("unitychan", 41, 42)]
        public void NormalCleanupReducesFaceBlotches(string c, int frontPct, int sidePct)
        {
            if (comps == null || !comps.ContainsKey(c)) Assert.Ignore(c + ": 테스트셋 또는 외곽선 피처가 없습니다 (저장소 밖 파일)");
            int[] before = comps[c]["mix0.0"], after = comps[c]["mix1.0"];
            string got = string.Format("{0} 정면 {1}→{2}, 측면 {3}→{4}", c, before[0], after[0], before[1], after[1]);
            Assert.That(Reduction(before[0], after[0]), Is.GreaterThanOrEqualTo(frontPct - 10f), got);
            Assert.That(Reduction(before[1], after[1]), Is.GreaterThanOrEqualTo(sidePct - 10f), got);
            Assert.Pass(got);
        }

        static float Reduction(int before, int after) { return before > 0 ? 100f * (before - after) / before : 0f; }
    }
}
