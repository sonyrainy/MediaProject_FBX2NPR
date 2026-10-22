// FBX2NPRLineFadePatch.cs — 이미 변환한 툰 프리팹의 피부 메시(얼굴 재질을 받는 메시)에 '안쪽 선 지우기 가중치'(UV2.y)를 넣는다 (2026-10-14).
// 변환 도구는 이제 변환할 때 UV2.y 를 같이 쓴다(FBX2NPRConverter.LineFadeWeights). 이 메뉴는 다시 변환하지 않고 기존 메시에만 같은 값을 넣는다 —
// 다시 변환하면 파싱·평탄화·텍스처까지 새로 만들어져 최종본의 다른 부분이 바뀔 수 있어서.
// 배치: rungen.ps1 -Method FBX2NPR.EditorTools.FBX2NPRLineFadePatch.Run  (환경변수 FBX2NPR_LINEFADE_MODELS=Ch07;TheBoss;... 기본 네 캐릭터)
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRLineFadePatch
    {
        static readonly string[] DefaultModels = { "Ch07", "TheBoss", "Jones", "unitychan" };

        [MenuItem("FBX2NPR/게임 적용 검증/피부 안쪽 선 지우기 가중치 넣기 (변환한 툰 프리팹)")]
        public static void Run()
        {
            var log = new List<string>();
            string env = Environment.GetEnvironmentVariable("FBX2NPR_LINEFADE_MODELS");
            var models = string.IsNullOrEmpty(env) ? DefaultModels : env.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            try
            {
                foreach (var model in models) Patch(model.Trim(), log);
                AssetDatabase.SaveAssets();
            }
            catch (Exception ex) { log.Add("예외: " + ex); }
            finally
            {
                string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "NormalTestOut", "neckfix"); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "patch_log.txt"), string.Join("\n", log.ToArray()), new UTF8Encoding(true));
                foreach (var l in log) Debug.Log("[LineFade] " + l);
            }
        }

        static void Patch(string model, List<string> log)
        {
            string prefabPath = FBX2NPRRuntimeCheck.PrefabPath(model + "_Toon");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { log.Add(model + ": 툰 프리팹 없음 " + prefabPath); return; }
            log.Add(model + ": 프리팹 " + prefabPath);
            var renderers = new List<Renderer>(prefab.GetComponentsInChildren<Renderer>(true));
            foreach (var r in renderers)
            {
                Mesh mesh = null;
                var smr = r as SkinnedMeshRenderer; if (smr != null) mesh = smr.sharedMesh;
                else { var mf = r.GetComponent<MeshFilter>(); if (mf != null) mesh = mf.sharedMesh; }
                if (mesh == null) continue;
                var mats = r.sharedMaterials;
                int n = Mathf.Max(1, Mathf.Min(mats.Length, mesh.subMeshCount));
                var faceSlot = new bool[n]; int faceN = 0;
                for (int mi = 0; mi < n; mi++)
                {
                    var m = mi < mats.Length ? mats[mi] : null;
                    faceSlot[mi] = m != null && m.HasProperty("_FaceWeightOn") && m.GetFloat("_FaceWeightOn") > 0.5f;
                    if (faceSlot[mi]) faceN++;
                }
                if (faceN == 0) continue;   // 얼굴 재질을 받지 않는 메시는 UV2.y 를 읽지 않는다
                string mp = AssetDatabase.GetAssetPath(mesh);
                if (!mp.StartsWith(FBX2NPRConverter.OutRoot)) { log.Add("  " + mesh.name + ": 변환 도구가 만든 메시가 아님, 건너뜀 (" + mp + ")"); continue; }
                int vc = mesh.vertexCount;
                var y = FBX2NPRConverter.LineFadeWeights(vc);
                if (faceN < n)
                {
                    // 변환 도구와 같은 규칙: 얼굴 재질 칸의 삼각형에 쓰인 정점만
                    var keep = new bool[vc];
                    for (int mi = 0; mi < Mathf.Min(n, mesh.subMeshCount); mi++) if (faceSlot[mi]) foreach (int idx in mesh.GetTriangles(mi)) keep[idx] = true;
                    for (int i = 0; i < vc; i++) if (!keep[i]) y[i] = 0f;
                }
                var uv2 = new List<Vector2>(); mesh.GetUVs(2, uv2);
                if (uv2.Count != vc) { log.Add("  " + mesh.name + ": UV2 개수가 정점 수와 다름 (" + uv2.Count + "/" + vc + "), 건너뜀"); continue; }
                int nY = 0, nWider = 0, nLess = 0;
                for (int i = 0; i < vc; i++)
                {
                    if (y[i] > 0f) nY++;
                    if (y[i] > uv2[i].x + 0.01f) nWider++;     // 얼굴 가중치보다 넓어진 정점 = 새로 덮이는 피부
                    if (y[i] + 0.01f < uv2[i].x) nLess++;      // 있으면 안 된다
                    uv2[i] = new Vector2(uv2[i].x, y[i]);
                }
                mesh.SetUVs(2, uv2);
                EditorUtility.SetDirty(mesh);
                log.Add(string.Format("  {0} ({1}): 정점 {2}, 얼굴 재질 칸 {3}/{4}, y>0 {5}, 얼굴보다 넓어진 정점 {6}, 얼굴보다 좁아진 정점 {7}",
                    mesh.name, mp, vc, faceN, n, nY, nWider, nLess));
            }
        }
    }
}
