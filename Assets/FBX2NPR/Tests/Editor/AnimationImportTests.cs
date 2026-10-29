using System.IO;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 믹사모 클립 임포트 보정(FBX2NPRHumanoidSetup)이 풀리지 않았는지 지킵니다.
    /// - 클립 파일의 뼈대로 아바타를 만들면 기준 자세가 T자세가 아니라서 왼발이 꼬였습니다 (걷기 110.6°, 달리기 173.9°).
    ///   원래 캐릭터의 T자세 아바타를 복사해야 합니다.
    /// - Running 은 실제 한 사이클(2~23 프레임)만 반복해야 골반이 내려앉지 않습니다 (0.362m → 0.003m).
    /// 테스트셋은 저장소에 올리지 않으므로, 없으면 건너뜁니다.
    /// </summary>
    public class AnimationImportTests
    {
        static string[] ClipFiles()
        {
            if (!AssetDatabase.IsValidFolder(FBX2NPRHumanoidSetup.AnimDir)) return new string[0];
            var guids = AssetDatabase.FindAssets("t:Model", new[] { FBX2NPRHumanoidSetup.AnimDir });
            var paths = new string[guids.Length];
            for (int i = 0; i < guids.Length; i++) paths[i] = AssetDatabase.GUIDToAssetPath(guids[i]);
            return paths;
        }

        [Test]
        public void ClipsCopyTPoseAvatarOfTheirCharacter()
        {
            int checkedClips = 0;
            foreach (var p in ClipFiles())
            {
                string src = FBX2NPRHumanoidSetup.ClipSourceModel(p);
                if (src == null) continue;   // 기준 캐릭터를 알 수 없는 클립은 셋업도 건너뜁니다
                checkedClips++;
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                Assert.That(mi.animationType, Is.EqualTo(ModelImporterAnimationType.Human), p);
                Assert.That(mi.avatarSetup, Is.EqualTo(ModelImporterAvatarSetup.CopyFromOther), p + ": 클립 뼈대로 아바타를 만들면 왼발이 꼬입니다.");
                Assert.That(mi.sourceAvatar, Is.Not.Null, p);
                Assert.That(AssetDatabase.GetAssetPath(mi.sourceAvatar), Is.EqualTo(FBX2NPRHumanoidSetup.ModelPath(src)), p + ": 기준 아바타가 원래 캐릭터의 것이 아닙니다.");
            }
            if (checkedClips == 0) Assert.Ignore("클립이 없습니다 (" + FBX2NPRHumanoidSetup.AnimDir + ", 저장소 밖 파일)");
        }

        [Test]
        public void LoopRangesKeepCleanCycle()
        {
            int checkedClips = 0;
            foreach (var p in ClipFiles())
            {
                var mi = (ModelImporter)AssetImporter.GetAtPath(p);
                string fn = Path.GetFileNameWithoutExtension(p);
                string atName = fn.IndexOf('@') >= 0 ? fn.Substring(fn.IndexOf('@') + 1) : fn;
                foreach (var clip in mi.clipAnimations)
                {
                    Vector2 range;
                    if (!FBX2NPRHumanoidSetup.LoopRanges.TryGetValue(clip.name, out range) && !FBX2NPRHumanoidSetup.LoopRanges.TryGetValue(atName, out range)) continue;
                    checkedClips++;
                    Assert.That(clip.firstFrame, Is.EqualTo(range.x), p + " 시작 프레임");
                    Assert.That(clip.lastFrame, Is.EqualTo(range.y), p + " 끝 프레임");
                    Assert.That(clip.loopTime, Is.True, p);
                }
            }
            if (checkedClips == 0) Assert.Ignore("반복 구간을 정한 클립이 없습니다.");
        }
    }
}
