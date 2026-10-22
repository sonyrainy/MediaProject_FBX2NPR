// FBX2NPRPortfolioFaces.cs — 포트폴리오 얼굴 비교용. 회화풍 얼굴을 선(외곽선) 두 가지 방식으로 다시 찍는다. 프리팹은 바꾸지 않는다(변환 없음).
//   ink     : 회화풍 리그 자신의 잉크 값 (깊이 선만, 안쪽 선 없음)
//   toonline: 툰 리그의 외곽선 값을 빌려 안쪽 선(노멀 엣지)까지 — 혼합 장면에서 회화풍 캐릭터가 받는 선과 같다 (기록 90번)
// 배치: -executeMethod FBX2NPR.EditorTools.FBX2NPRPortfolioFaces.Run, 환경변수 FBX2NPR_PF_CHARS (기본 "Ch07;TheBoss;Jones;unitychan"). 결과 NormalTestOut/portfolio_faces/.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRPortfolioFaces
    {
        static string OutDir { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "portfolio_faces"); } }

        static string FindModel(string name)
        {
            foreach (var g in AssetDatabase.FindAssets("t:Model", new[] { "Assets/TestSet" }))
            {
                var p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public static void Run()
        {
            Directory.CreateDirectory(OutDir); var log = new List<string>();
            var feat = FBX2NPRConverter.FindFeature(); bool featWas = feat != null && feat.isActive;
            float[] keep = feat != null ? new[] { feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold } : null;
            string env = Environment.GetEnvironmentVariable("FBX2NPR_PF_CHARS"); if (string.IsNullOrEmpty(env)) env = "Ch07;TheBoss;Jones;unitychan";
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                foreach (var c in env.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)) Shoot(c, feat, log);
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                if (feat != null && keep != null) { feat.settings.darkness = keep[0]; feat.settings.thickness = keep[1]; feat.settings.depthScale = keep[2]; feat.settings.normalScale = keep[3]; feat.settings.threshold = keep[4]; feat.SetActive(featWas); }
                File.WriteAllText(Path.Combine(OutDir, "log.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[PortfolioFaces] " + l);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static void Shoot(string c, SobelOutlineFeature feat, List<string> log)
        {
            string mp = FindModel(c); if (mp == null) { log.Add("모델 없음: " + c); return; }
            FBX2NPRRuntimeCheck.NewStage(false);
            var crit = FBX2NPRRuntimeCheck.Crit(c);
            var pbr = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(mp));   // 구도(머리 경계)를 원본과 똑같이 잡기 위해서만
            Bounds head; int hv; if (!FBX2NPRParseBake.HeadBounds(pbr, crit, out head, out hv)) head = FBX2NPRRuntimeCheck.HeadOf(pbr, c, new List<string>());
            UnityEngine.Object.DestroyImmediate(pbr);
            var toonP = AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath(c + "_Toon"));
            var paintP = AssetDatabase.LoadAssetAtPath<GameObject>(FBX2NPRRuntimeCheck.PrefabPath(c + "_Painterly"));
            if (toonP == null || paintP == null) { log.Add("프리팹 없음: " + c); return; }
            var toon = (GameObject)PrefabUtility.InstantiatePrefab(toonP); var paint = (GameObject)PrefabUtility.InstantiatePrefab(paintP);
            var trig = toon.GetComponentInChildren<ToonFaceRig>(true); var prig = paint.GetComponentInChildren<PainterlyRig>(true);

            // 1) ink: 툰을 끄고 회화풍 자기 값
            toon.SetActive(false); paint.SetActive(false); paint.SetActive(true);
            if (feat != null) { prig.ApplyInk(); feat.settings.thickness = prig.inkThickness * 2f; }
            FBX2NPRCompare.SetSS(2);
            FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + "_paint_ink_face.png"), Vector3.forward, false, 1.18f);
            log.Add(c + " ink: " + (feat != null ? string.Format("darkness {0:F2} thickness {1:F2} depth {2:F1} normal {3:F2} threshold {4:F2}", feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold) : "피처 없음"));

            // 2) toonline: 툰 리그를 켠 채 렌더러만 꺼서 외곽선 값을 툰 쪽으로 (회화풍 리그는 양보), 회화풍만 보이게
            paint.SetActive(false); toon.SetActive(true);
            var tr = toon.GetComponentsInChildren<Renderer>(true); var wasOn = new List<bool>(); foreach (var r in tr) { wasOn.Add(r.enabled); r.enabled = false; }
            paint.SetActive(true);
            float baseThick = 1f; if (feat != null && trig != null) { trig.ApplyOutline(); baseThick = trig.outlineThickness; feat.settings.thickness = baseThick * 2f; }
            FBX2NPRCompare.SetSS(2);
            FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + "_paint_toonline_face.png"), Vector3.forward, false, 1.18f);
            log.Add(c + " toonline: " + (feat != null ? string.Format("darkness {0:F2} thickness {1:F2} depth {2:F1} normal {3:F2} threshold {4:F2}", feat.settings.darkness, feat.settings.thickness, feat.settings.depthScale, feat.settings.normalScale, feat.settings.threshold) : "피처 없음"));
            // 3) 더 굵고 진한 선 (10/11 요청): 환경변수 FBX2NPR_PF_LINES = "두께,진하기;두께,진하기" (기본 "3.0,0.55;4.0,0.75") → _paint_lineA/_lineB
            string lines = Environment.GetEnvironmentVariable("FBX2NPR_PF_LINES"); if (string.IsNullOrEmpty(lines)) lines = "3.0,0.55;4.0,0.75";
            if (feat != null && trig != null)
            {
                var sp = lines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k < sp.Length; k++)
                {
                    var pr = sp[k].Split(',');
                    float th = float.Parse(pr[0], System.Globalization.CultureInfo.InvariantCulture), dk = float.Parse(pr[1], System.Globalization.CultureInfo.InvariantCulture);
                    trig.ApplyOutline(); feat.settings.thickness = th * 2f; feat.settings.darkness = dk;
                    if (pr.Length > 2) feat.settings.normalScale = float.Parse(pr[2], System.Globalization.CultureInfo.InvariantCulture);   // 세 번째 값: 안쪽(노멀 엣지) 선 세기
                    FBX2NPRCompare.SetSS(2);
                    string tag = ((char)('A' + k)).ToString();
                    FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + "_paint_line" + tag + "_face.png"), Vector3.forward, false, 1.18f);
                    log.Add(c + " line" + tag + ": thickness " + th + " (x2 SS) darkness " + dk + " depth " + feat.settings.depthScale + " normal " + feat.settings.normalScale + " threshold " + feat.settings.threshold);
                }
            }

            // 4) 툰 얼굴(코 그림자 끔) — 콧볼 얼룩 확인용. 툰 리그를 켜고 회화풍은 끈다.
            if (Environment.GetEnvironmentVariable("FBX2NPR_PF_NONOSE") == "1" && trig != null)
            {
                paint.SetActive(false); foreach (var r in tr) r.enabled = true;
                // noseShadow 를 끄면 셰이더 변형이 바뀌어 입술까지 달라진다 → 켠 채 세기만 0 으로 (_NoseShade = 0)
                // 최종 시트(FBX2NPRFinalSheet)와 같은 순서: 외곽선 값 → 껐다 켜기 → Apply → 촬영
                float nsOld = trig.noseStrength;
                foreach (var pair in new[] { new { s = nsOld, f = "_toon_again_face.png" }, new { s = 0f, f = "_toon_nonose_face.png" } })
                {
                    trig.noseStrength = pair.s;
                    if (feat != null) { trig.ApplyOutline(); feat.settings.thickness = trig.outlineThickness * 2f; }
                    toon.SetActive(false); toon.SetActive(true); trig.Apply();
                    FBX2NPRCompare.SetSS(2);
                    FBX2NPRCompare.Shoot(head, 700, 700, Path.Combine(OutDir, c + pair.f), Vector3.forward, false, 1.18f);
                }
                trig.noseStrength = nsOld;
                log.Add(c + " toon again/nonose rendered (noseStrength " + nsOld + " / 0)");
            }
            UnityEngine.Object.DestroyImmediate(toon); UnityEngine.Object.DestroyImmediate(paint);
        }
    }
}
