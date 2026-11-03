using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 툰 리그 조정값 저장(FBX2NPRAssetSettings.toonRig)을 지킵니다.
    /// 저장한 값이 모델 설정 JSON 을 거쳐 그대로 돌아오는지, 예전 설정에는 저장값이 없는지(다시 변환해도 예전과 같은 결과),
    /// 저장 항목이 리그 필드와 이름·형이 같은지 봅니다.
    /// </summary>
    public class RigValuesTests
    {
        readonly System.Collections.Generic.List<GameObject> made = new System.Collections.Generic.List<GameObject>();

        ToonFaceRig NewRig()
        {
            var go = new GameObject("rig test"); go.SetActive(false);   // 꺼 두어 OnEnable(Apply) 이 돌지 않게
            made.Add(go);
            return go.AddComponent<ToonFaceRig>();
        }

        [TearDown]
        public void Clean() { foreach (var go in made) Object.DestroyImmediate(go); made.Clear(); }

        static void AssertSame(object want, object got, string name)
        {
            if (want is float fw) Assert.That((float)got, Is.EqualTo(fw).Within(1e-5f), name);
            else if (want is Color cw) Assert.That(Vector4.Distance(cw, (Color)got), Is.LessThan(1e-5f), name);
            else Assert.That(got, Is.EqualTo(want), name);
        }

        [Test]
        public void SavedValuesComeBackThroughModelSettings()
        {
            var rig = NewRig();
            rig.outline = false; rig.outlineDarkness = 0.61f; rig.outlineThickness = 2.5f; rig.noseLineFade = 0.7f;
            rig.linePosition = -0.42f; rig.chinTilt = 0.4f; rig.chinBend = 0.8f; rig.lineSoftness = 0.05f; rig.rim = 0.9f;
            rig.urpLighting = true; rig.faceReceiveShadows = 0.3f; rig.bodyReceiveShadows = 0.2f;
            rig.skinShadow = new Color(0.86f, 0.62f, 0.60f, 1f); rig.bodyShadow = 0.6f; rig.bodyShadowColor = new Color(0.3f, 0.3f, 0.4f, 1f);

            var s = new FBX2NPRAssetSettings { toonRigSaved = true, toonRig = ToonRigValues.From(rig) };
            var back = FBX2NPRAssetSettings.FromJson(s.ToJson());
            Assert.That(back.toonRigSaved, Is.True);

            var rig2 = NewRig();
            back.toonRig.ApplyTo(rig2);
            foreach (var f in typeof(ToonRigValues).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var rf = typeof(ToonFaceRig).GetField(f.Name);
                AssertSame(rf.GetValue(rig), rf.GetValue(rig2), f.Name);
            }
        }

        [Test]
        public void OldSettingsHaveNoSavedValues()
        {
            // 이 기능 전에 저장된 모델 설정: 저장값이 없으므로 변환 결과는 예전과 같다 (코 둘레 선은 toonNoseLineFade 그대로)
            var s = FBX2NPRAssetSettings.FromJson("{\"headBoneContains\":\"head\",\"toonNoseLineFade\":0.7}");
            Assert.That(s.toonRigSaved, Is.False);
            Assert.That(s.toonNoseLineFade, Is.EqualTo(0.7f).Within(1e-6f));
        }

        [Test]
        public void EverySavedValueIsARigField()
        {
            foreach (var f in typeof(ToonRigValues).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var rf = typeof(ToonFaceRig).GetField(f.Name, BindingFlags.Public | BindingFlags.Instance);
                Assert.That(rf, Is.Not.Null, f.Name + ": 리그에 같은 이름의 필드가 없습니다.");
                Assert.That(rf.FieldType, Is.EqualTo(f.FieldType), f.Name + ": 리그 필드와 형이 다릅니다.");
            }
        }
    }
}
