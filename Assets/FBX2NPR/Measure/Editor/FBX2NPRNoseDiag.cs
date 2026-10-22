// FBX2NPRNoseDiag.cs — Ch07 툰 콧볼 얼룩이 어느 층(외곽선 / 코 음영 / 텍스처)에서 나오는지 가르는 확대 렌더. 프리팹은 바꾸지 않는다.
// 배치: -executeMethod FBX2NPR.EditorTools.FBX2NPRNoseDiag.Run, 환경변수 FBX2NPR_ND_CHAR(기본 Ch07). 결과 NormalTestOut/nose_diag/.
using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRNoseDiag
    {
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "nose_diag"); } }

        public static void Run()
        {
            Directory.CreateDirectory(OutDir);
            string c = Environment.GetEnvironmentVariable("FBX2NPR_ND_CHAR"); if (string.IsNullOrEmpty(c)) c = "Ch07";
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                FBX2NPRRuntimeCheck.NewStage(false);
                var toonP = AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath(c + "_Toon"));
                var toon = (GameObject)PrefabUtility.InstantiatePrefab(toonP);
                var rig = toon.GetComponentInChildren<ToonFaceRig>(true);
                Vector3 tip = rig.noseTip != null ? rig.noseTip.position : toon.transform.position;
                var bb = new Bounds(tip + new Vector3(0, 0.012f, 0), new Vector3(0.10f, 0.08f, 0.08f));
                float noseStr = rig.noseStrength;
                FBX2NPRCompare.SetSS(3);
                if (feat != null) { rig.ApplyOutline(); feat.SetActive(true); }
                Shot(bb, "a_base");
                if (feat != null) { feat.SetActive(false); Shot(bb, "b_nooutline"); feat.SetActive(true); }
                rig.noseStrength = noseStr; rig.noseShadow = false; rig.Apply(); if (feat != null) { feat.SetActive(false); Shot(bb, "c_noshade_nooutline"); feat.SetActive(true); Shot(bb, "d_noshade_outline"); }
                rig.noseShadow = true; rig.Apply();
                var log = "rig " + rig.name + " tip " + tip + " noseStrength " + noseStr + " noseFigure " + rig.noseFigure + " halfMoon " + rig.noseHalfMoon + " driveOutline " + rig.driveOutlineSettings;
                File.WriteAllText(Path.Combine(OutDir, "log.txt"), log); Debug.Log("[NoseDiag] " + log);
                UnityEngine.Object.DestroyImmediate(toon);
            }
            catch (Exception ex) { Debug.LogError("[NoseDiag] " + ex); File.WriteAllText(Path.Combine(OutDir, "log.txt"), ex.ToString()); }
            finally { if (feat != null) feat.SetActive(featWas); EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); }
        }

        static void Shot(Bounds bb, string name) { FBX2NPRCompare.Shoot(bb, 900, 720, Path.Combine(OutDir, name + ".png"), Vector3.forward, false, 1.05f); }
    }
}
