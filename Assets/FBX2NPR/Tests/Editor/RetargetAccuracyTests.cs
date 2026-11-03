using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 믹사모 클립을 Humanoid 로 재생한 결과가 원본과 같은지 숫자로 봅니다 (FBX2NPRFootDiag.RunVerifyActual 과 같은 비교).
    /// 원본 = 같은 FBX 를 Generic 으로 임포트해 Ch07 뼈대에 그대로 재생한 것 (리타깃이 없으니 믹사모가 만든 동작 그대로).
    /// 기준선: 걷기 왼발 회전 차 110.6° → 0.2° (8개 시각 평균), 달리기 골반 높이 차 0.362m → 0.003m (최대).
    /// Generic 사본은 임시 폴더에 만들고 끝나면 지웁니다. 테스트셋이 없으면 건너뜁니다.
    /// </summary>
    public class RetargetAccuracyTests
    {
        const string Dir = "Assets/FBX2NPRTestTemp";
        GameObject humanoid, generic;

        [TearDown]
        public void TearDown()
        {
            if (humanoid != null) Object.DestroyImmediate(humanoid);
            if (generic != null) Object.DestroyImmediate(generic);
            AssetDatabase.DeleteAsset(Dir);
        }

        static string ClipPath(string clip) { return FBX2NPRHumanoidSetup.AnimDir + "/Ch07_nonPBR@" + clip + ".fbx"; }

        static bool Ready(string src, out string model)
        {
            model = FBX2NPRHumanoidSetup.ModelPath("Ch07");
            return model != null && AssetDatabase.LoadMainAssetAtPath(src) != null;
        }

        static AnimationClip GenericCopy(string src)
        {
            FBX2NPRConverter.EnsureFolder(Dir);
            string dst = Dir + "/" + System.IO.Path.GetFileName(src);
            AssetDatabase.CopyAsset(src, dst);
            var mi = (ModelImporter)AssetImporter.GetAtPath(dst);
            mi.animationType = ModelImporterAnimationType.Generic; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; mi.importAnimation = true;
            var cl = mi.defaultClipAnimations;
            for (int i = 0; i < cl.Length; i++) cl[i].loopTime = true;
            mi.clipAnimations = cl; mi.SaveAndReimport();
            return FBX2NPRFootDiag.Clip(dst);
        }

        // 뼈는 애니메이터가 있을 때 휴머노이드 매핑으로 찾아 두고, 원본 쪽은 그다음 애니메이터를 지워 변환 곡선만 받게 합니다.
        static GameObject Spawn(string model, bool asGeneric, out Transform[] bones)
        {
            var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(model));
            go.hideFlags = HideFlags.HideAndDontSave;
            bones = FBX2NPRFootDiag.Bones(go);
            if (asGeneric) { var an = go.GetComponent<Animator>(); if (an != null) Object.DestroyImmediate(an); }
            return go;
        }

        static Quaternion FromHips(Transform[] b, int i) { return Quaternion.Inverse(b[6].rotation) * b[i].rotation; }

        [Test]
        public void WalkingLeftFootMatchesOriginal()
        {
            string src = ClipPath("Walking"), model;
            if (!Ready(src, out model)) Assert.Ignore("테스트셋이 없습니다 (저장소 밖 파일)");
            var clipA = FBX2NPRFootDiag.Clip(src);
            var clipB = GenericCopy(src);
            Transform[] ba, bb;
            humanoid = Spawn(model, false, out ba);
            generic = Spawn(model, true, out bb);

            const int N = 8;
            double sum = 0;
            for (int k = 0; k < N; k++)
            {
                FBX2NPRFootDiag.SampleHuman(humanoid, clipA, k / (float)N * clipA.length);
                clipB.SampleAnimation(generic, k / (float)N * clipB.length);
                sum += Quaternion.Angle(FromHips(ba, 2), FromHips(bb, 2));   // 2 = 왼발
            }
            Assert.That(sum / N, Is.LessThan(1.0), "걷기 왼발 회전 차 평균(도)");
            Assert.Pass(string.Format("걷기 왼발 회전 차 평균 {0:F2}°", sum / N));
        }

        [Test]
        public void RunningPelvisHeightMatchesOriginal()
        {
            string src = ClipPath("Running"), model;
            if (!Ready(src, out model)) Assert.Ignore("테스트셋이 없습니다 (저장소 밖 파일)");
            var clipA = FBX2NPRFootDiag.Clip(src);
            var clipB = GenericCopy(src);
            Transform[] ba, bb;
            humanoid = Spawn(model, false, out ba);
            generic = Spawn(model, true, out bb);

            // 클립이 반복 구간을 잘라 쓰면(firstFrame) 원본 시각 = firstFrame/fps + t
            var cas = ((ModelImporter)AssetImporter.GetAtPath(src)).clipAnimations;
            float first = cas.Length > 0 ? cas[0].firstFrame : 0f, fr = clipB.frameRate;
            int frames = Mathf.Max(2, Mathf.RoundToInt(clipA.length * 60f));
            double maxH = 0;
            for (int f = 0; f < frames; f++)
            {
                float t = f / 60f;
                FBX2NPRFootDiag.SampleHuman(humanoid, clipA, t);
                clipB.SampleAnimation(generic, Mathf.Min(clipB.length, first / fr + t));
                maxH = System.Math.Max(maxH, Mathf.Abs(FBX2NPRFootDiag.PelvisHeight(ba) - FBX2NPRFootDiag.PelvisHeight(bb)));
            }
            Assert.That(maxH, Is.LessThan(0.01), "달리기 골반 높이 차 최대(m)");
            Assert.Pass(string.Format("달리기 골반 높이 차 최대 {0:F4}m", maxH));
        }
    }
}
