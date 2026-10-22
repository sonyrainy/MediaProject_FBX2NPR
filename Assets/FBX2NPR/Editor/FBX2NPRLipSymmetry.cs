// FBX2NPRLipSymmetry.cs — 렌더 공간 아랫입술 대칭 보완 (기록 95번 추가 6).
// UV 가 좌우 비대칭인 모델(Ch07)은 텍스처를 대칭으로 만들어도 렌더에서 한쪽 아랫입술 날개가 비어 보인다. 그래서 정면 렌더에서
//   '온전한 쪽 아랫입술을 거울로 비춘 영역 중 비어 있는 반대쪽 픽셀'을 구하고, 그 픽셀이 쓰는 텍셀만 입술색으로 칠한다.
// 픽셀 → 텍셀 대응은 URP 카메라가 아니라 Graphics.DrawMeshNow + float RT 로 UV 를 직접 그려 읽는다 (ParseBake 의 데이터 패스와 같은 방식 — URP 를 거치면 8비트·sRGB 양자화로 번호가 깨졌다).
// 윗입술·반대쪽·그 밖의 텍셀은 비트 단위로 그대로다. 사용자 지시: "왼쪽은 1픽셀도 건드리지 말고, 틈새선 아래 오른쪽 아랫입술만 왼쪽과 대칭으로".
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    public static class FBX2NPRLipSymmetry
    {
        const int R = 1024;          // 정면 렌더 해상도 (700² 비교 샷보다 촘촘하게 — 텍셀 하나가 픽셀 2개쯤)
        const float FovY = 24f;      // FBX2NPRCompare.Shoot 과 같은 구도

        /// <summary>r.albedo 의 텍셀을 제자리에서 고치고 r.labelDone 에 표시한다. 반환: 기록 한 줄 (아무것도 안 했으면 null).</summary>
        public static string Apply(GameObject inst, Bounds head, Texture albedo, SkinFlatBake.Result r, FBX2NPRAssetSettings crit, string modelPath)
        {
            var d = r.drawn; int side = crit.toonLipSymmetrySide;
            if (side == 0 || d == null || d.lipFill == null || d.lipLowerFill == null || r.albedo == null) return null;
            int W = r.albedo.width, H = r.albedo.height, N = W * H;
            var sh = Shader.Find("FBX2NPR/UVData"); if (sh == null) return "입술 대칭: FBX2NPR/UVData 셰이더 없음";
            Material mat = null; RenderTexture rt = null; GameObject camGo = null; Texture2D shot = null; var temps = new List<Mesh>();
            try
            {
                // 1) 얼굴 알베도를 쓰는 서브메시만 모은다 (스킨 메시는 지금 자세로 굽는다 — ParseBake 와 같은 규칙)
                var slots = new List<KeyValuePair<Mesh, KeyValuePair<Matrix4x4, int>>>();
                foreach (var rend in inst.GetComponentsInChildren<Renderer>(true))
                {
                    if (!rend.gameObject.activeInHierarchy || !rend.enabled) continue;
                    if (crit.IsIgnoredMesh(rend.name)) continue;
                    Mesh mesh = null; Matrix4x4 l2w = rend.transform.localToWorldMatrix;
                    var smr = rend as SkinnedMeshRenderer;
                    if (smr != null) { if (smr.sharedMesh == null) continue; mesh = new Mesh(); smr.BakeMesh(mesh, true); temps.Add(mesh); }
                    else { var mf = rend.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue; mesh = mf.sharedMesh; }
                    var mats = rend.sharedMaterials;
                    for (int s = 0; s < mesh.subMeshCount; s++)
                    {
                        var m = mats.Length > 0 ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                        if (FBX2NPRConverter.ResolveAlbedo(m, modelPath, crit) != albedo) continue;
                        slots.Add(new KeyValuePair<Mesh, KeyValuePair<Matrix4x4, int>>(mesh, new KeyValuePair<Matrix4x4, int>(l2w, s)));
                    }
                }
                if (slots.Count == 0) return "입술 대칭: 얼굴 알베도를 쓰는 서브메시 없음";
                // 2) 정면 카메라 행렬 (FBX2NPRCompare.Shoot 구도) → UV 데이터 패스
                camGo = new GameObject("LipSymCam"); camGo.hideFlags = HideFlags.HideAndDontSave;
                var cam = camGo.AddComponent<Camera>(); cam.enabled = false; cam.fieldOfView = FovY; cam.aspect = 1f;
                float halfH = Mathf.Max(head.size.y * 0.5f, head.size.x * 0.5f);
                float dist = halfH / Mathf.Tan(FovY * 0.5f * Mathf.Deg2Rad) * 1.18f + head.size.z * 0.5f;
                camGo.transform.position = head.center + Vector3.forward * dist;
                camGo.transform.rotation = Quaternion.LookRotation(head.center - camGo.transform.position, Vector3.up);
                float rad = head.extents.magnitude; cam.nearClipPlane = Mathf.Max(0.01f, dist - rad * 3f); cam.farClipPlane = dist + rad * 6f;
                mat = new Material(sh); mat.hideFlags = HideFlags.HideAndDontSave;
                rt = new RenderTexture(R, R, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear); rt.Create();
                var prev = RenderTexture.active;
                Graphics.SetRenderTarget(rt);
                GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
                GL.PushMatrix();
                GL.LoadProjectionMatrix(cam.projectionMatrix);
                GL.modelview = cam.worldToCameraMatrix;
                foreach (var sl in slots) { if (!mat.SetPass(0)) continue; Graphics.DrawMeshNow(sl.Key, sl.Value.Key, sl.Value.Value); }
                GL.PopMatrix();
                shot = new Texture2D(R, R, TextureFormat.RGBAFloat, false, true);
                shot.ReadPixels(new Rect(0, 0, R, R), 0, 0); shot.Apply();
                RenderTexture.active = prev;
                var uv = shot.GetPixelData<float>(0);   // 4 float/화소, 행 0 = 아래
                // 3) 픽셀 → 텍셀, 렌더 공간 마스크
                int P = R * R; var tex = new int[P]; var fill = new bool[P]; var lower = new bool[P]; var upper = new bool[P]; int seen = 0;
                for (int p = 0; p < P; p++)
                {
                    if (uv[p * 4 + 3] < 0.5f) { tex[p] = -1; continue; }
                    float u = uv[p * 4] - Mathf.Floor(uv[p * 4]), v = uv[p * 4 + 1] - Mathf.Floor(uv[p * 4 + 1]);
                    int tx = Mathf.Clamp((int)(u * W), 0, W - 1), ty = Mathf.Clamp((int)(v * H), 0, H - 1);
                    int t = ty * W + tx; tex[p] = t; seen++; fill[p] = d.lipFill[t]; lower[p] = d.lipLowerFill[t]; upper[p] = d.lipUpperFill[t];
                }
                // 축: 윗입술 렌더 마스크 상자의 가운데 (윗입술은 온전하다). 없으면 채움 전체
                int ux0 = R, ux1 = -1, fx0 = R, fx1 = -1, lowerN = 0, upperN = 0, fillN = 0;
                for (int p = 0; p < P; p++) { int x = p % R; if (upper[p]) { upperN++; if (x < ux0) ux0 = x; if (x > ux1) ux1 = x; } if (fill[p]) { fillN++; if (x < fx0) fx0 = x; if (x > fx1) fx1 = x; } if (lower[p]) lowerN++; }
                if (lowerN == 0 || fx1 < 0) return "입술 대칭: 렌더에 입술이 없음 (보인 픽셀 " + seen + ")";
                float xc = ux1 >= 0 ? 0.5f * (ux0 + ux1) : 0.5f * (fx0 + fx1);
                // 4) 보탤 픽셀: 반대쪽 아랫입술의 거울상 중 비어 있는 것, 지정한 화면 쪽(+1 = 픽셀 x 큰 쪽)만. 윗입술 픽셀은 제외
                var addPx = new bool[P]; int addN = 0;
                for (int y = 0; y < R; y++) for (int x = 0; x < R; x++)
                {
                    if (side > 0 ? x <= xc : x >= xc) continue;
                    int xm = Mathf.RoundToInt(2f * xc - x); if (xm < 0 || xm >= R) continue;
                    int p = y * R + x, q = y * R + xm;
                    if (lower[q] && !fill[p] && !upper[p] && tex[p] >= 0) { addPx[p] = true; addN++; }
                }
                if (!string.IsNullOrEmpty(FBX2NPRConverter.DebugDir))
                {
                    // 진단: 렌더 공간 마스크 (R 채움, G 아랫입술, B 윗입술, 흰색 = 보탤 픽셀)
                    var dbg = new Color32[P];
                    for (int p = 0; p < P; p++) dbg[p] = addPx[p] ? new Color32(255, 255, 255, 255) : new Color32((byte)(fill[p] ? 255 : 0), (byte)(lower[p] ? 255 : 0), (byte)(upper[p] ? 255 : 0), 255);
                    var tdbg = new Texture2D(R, R, TextureFormat.RGBA32, false); tdbg.SetPixels32(dbg); tdbg.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(FBX2NPRConverter.DebugDir, "lipsym_masks.png"), tdbg.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(tdbg);
                }
                // 5) 픽셀 → 텍셀. 기존 채움 둘레 1.2×기준길이 안으로 제한, 1텍셀 닫기로 자잘한 구멍을 메운다. 윗입술 텍셀은 절대 안 건드린다
                var addTex = new bool[N]; int near = Mathf.Max(2, Mathf.RoundToInt(1.2f * r.hwTex));
                for (int p = 0; p < P; p++) if (addPx[p]) addTex[tex[p]] = true;
                var ring = Dilate(d.lipFill, W, H, near);
                var closed = Erode(Dilate(addTex, W, H, 1), W, H, 1);
                int texN = 0; var col = d.lipColor ?? new byte[] { 181, 118, 105 };
                var px = r.albedo.GetPixels32();
                for (int i = 0; i < N; i++)
                {
                    if (!(addTex[i] || closed[i]) || !ring[i] || d.lipFill[i] || d.lipUpperFill[i]) continue;
                    px[i] = new Color32(col[0], col[1], col[2], px[i].a); texN++;
                    if (r.labelDone != null) r.labelDone[i] = true;
                    if (d.painted != null) d.painted[i] = true;
                }
                if (texN > 0) { r.albedo.SetPixels32(px); r.albedo.Apply(false, false); }
                return string.Format(CultureInfo.InvariantCulture, "입술 대칭(렌더 공간, 화면 {0}쪽 아랫입술): 축 x={1:F1}/{2}, 렌더 픽셀 채움 {3}·아랫 {4}·윗 {5}, 보탤 픽셀 {6} → 텍셀 {7} (보인 픽셀 {8}, 서브메시 {9})",
                    side > 0 ? "오른" : "왼", xc, R, fillN, lowerN, upperN, addN, texN, seen, slots.Count);
            }
            finally
            {
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (rt != null) { rt.Release(); UnityEngine.Object.DestroyImmediate(rt); }
                if (shot != null) UnityEngine.Object.DestroyImmediate(shot);
                if (mat != null) UnityEngine.Object.DestroyImmediate(mat);
                foreach (var m in temps) if (m != null) UnityEngine.Object.DestroyImmediate(m);
            }
        }

        static bool[] Dilate(bool[] m, int W, int H, int rad)
        {
            var o = (bool[])m.Clone();
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                if (!m[y * W + x]) continue;
                for (int dy = -rad; dy <= rad; dy++) for (int dx = -rad; dx <= rad; dx++)
                {
                    int xx = x + dx, yy = y + dy; if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                    if (dx * dx + dy * dy <= rad * rad) o[yy * W + xx] = true;
                }
            }
            return o;
        }
        static bool[] Erode(bool[] m, int W, int H, int rad)
        {
            var inv = new bool[m.Length]; for (int i = 0; i < m.Length; i++) inv[i] = !m[i];
            var dd = Dilate(inv, W, H, rad); var o = new bool[m.Length]; for (int i = 0; i < m.Length; i++) o[i] = !dd[i]; return o;
        }
    }
}
