using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 변환한 얼굴 노멀을 블렌더에서 손으로 다듬고 다시 가져오는 왕복 (2026-10-31).
    ///
    /// 메시를 FBX 로 통째로 내보내지 않고 얼굴 영역(UV2.x 얼굴 가중치가 세 꼭짓점 모두 0 보다 큰 삼각형)만 OBJ 로 쓴다.
    /// 메시·뼈·원본 노멀은 유니티가 그대로 들고 있고 노멀 방향만 주고받는다. FBX·블렌더의 UV 는 두 칸뿐이라 원본 노멀(UV3·UV4, 세 칸)을 실어 보낼 수 없고,
    /// UV4 는 지금 노멀 기준(탄젠트 공간)이라 노멀이 바뀌면 다시 구워야 하기 때문이다. 블렌더 왕복에서 실제로 겪은 면 뒤집힘·크기 100배도 형상을 다시 가져오지 않으니 생기지 않는다.
    /// 돌아온 노멀은 원본 모델 옆 FaceNormalEdit 에 저장하고, 변환기가 방사형 노멀 대신 넣은 뒤 원본 노멀을 UV4 에 다시 굽는다.
    ///
    /// OBJ 는 오른손 좌표라 x 를 뒤집고 삼각형 감김을 바꾼다. 같은 자리의 정점은 하나로 합쳐(용접) 블렌더에서 UV 이음새 없이 이어지게 하고, 모서리마다 UV·노멀을 따로 둔다.
    /// 돌아올 때는 정점 위치로 용접 번호를 찾고, 면의 세 용접 번호로 원래 삼각형을 찾는다. 블렌더 5.2.2 기본 설정에서 정점·면 순서와 노멀이 그대로 유지됨을 확인했지만(2026-10-31 시험),
    /// 위치로 찾으면 순서가 바뀌어도 되고, 블렌더에서 축·크기 설정이 바뀌면 위치가 맞지 않아 바로 알아챈다.
    /// </summary>
    public static class FaceNormalRoundTrip
    {
        const float GridSize = 1e-4f;      // 돌아온 정점을 위치로 찾는 허용 거리 (0.1 mm). OBJ 는 소수 6자리라 반올림 오차가 훨씬 작다
        const float ChangedDeg = 0.01f;    // 이보다 덜 바뀐 노멀은 손대지 않은 것으로 본다 (글자로 오가며 생기는 반올림 오차). 손대지 않은 정점은 노멀이 비트 단위로 그대로 남는다
        const float MaxMissFrac = 0.01f;   // 위치가 맞지 않는 정점이 이 비율을 넘으면 축·크기가 바뀐 것으로 보고 불러오지 않는다
        const float SplitDeg = 1f;         // 한 정점의 모서리 노멀이 이보다 벌어져 있으면 블렌더에서 날카로운 모서리로 고친 것: 평균을 넣고 기록에 남긴다

        public static string DefaultFolder { get { return Path.Combine(Directory.GetParent(Application.dataPath).FullName, "FBX2NPRNormalEdit"); } }

        /// <summary>얼굴 렌더러 하나에서 내보내는 부분. 내보내기와 불러오기가 같은 규칙으로 만든다.</summary>
        public class Piece
        {
            public string name, objName; public Mesh mesh;
            public Matrix4x4 m2r;                                              // 메시 좌표 → 모델 루트 좌표
            public List<int> tris = new List<int>();                           // 내보내는 삼각형 (메시 정점 번호, 유니티 감김)
            public int[] weld;                                                 // 메시 정점 → 용접 번호 (내보내지 않는 정점은 -1)
            public List<Vector3> weldPos = new List<Vector3>();                // 용접 번호 → 루트 좌표 위치
            public List<int> used = new List<int>();                           // 내보내는 메시 정점 (vt·vn 순서)
            public Dictionary<int, int> local = new Dictionary<int, int>();    // 메시 정점 → used 번호
        }

        public class ReadResult
        {
            public List<FaceNormalEdit.Entry> entries = new List<FaceNormalEdit.Entry>();   // 바뀐 정점만
            public string error;                                                            // 불러오지 않은 이유. null 이면 읽음
            public List<string> lines = new List<string>();
        }

        static FBX2NPRAssetSettings SettingsOf(ToonFaceRig rig)
        {
            string path = string.IsNullOrEmpty(rig.sourceModelGuid) ? null : AssetDatabase.GUIDToAssetPath(rig.sourceModelGuid);
            var imp = string.IsNullOrEmpty(path) ? null : AssetImporter.GetAtPath(path);
            return FBX2NPRAssetSettings.FromJson(imp != null ? imp.userData : null) ?? new FBX2NPRAssetSettings();
        }

        static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "") sb.Append(char.IsLetterOrDigit(ch) && ch < 128 || ch == '_' ? ch : '_');
            return sb.ToString();
        }

        /// <summary>리그의 얼굴 렌더러마다 내보낼 부분. 메시 좌표는 변환기와 같은 규칙(스킨 메시는 본 x 바인드 포즈)으로 모델 루트 좌표에 옮긴다.</summary>
        public static List<Piece> Pieces(ToonFaceRig rig)
        {
            var list = new List<Piece>();
            var root = rig.transform.root;
            var headBone = FBX2NPRConverter.FindHeadBone(root, SettingsOf(rig).headBoneContains);
            foreach (var r in rig.faceRenderers)
            {
                if (r == null) continue;
                Mesh mesh; Matrix4x4 l2w = r.transform.localToWorldMatrix;
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) { mesh = smr.sharedMesh; float diff; if (mesh != null) l2w = FBX2NPRConverter.BindToWorld(smr, mesh, headBone, out diff); }
                else { var mf = r.GetComponent<MeshFilter>(); mesh = mf != null ? mf.sharedMesh : null; }
                if (mesh == null) continue;
                var w = new List<Vector2>(); mesh.GetUVs(2, w);   // 변환기가 UV2.x 에 실은 얼굴 가중치
                if (w.Count != mesh.vertexCount) continue;
                var p = new Piece { name = r.name, mesh = mesh, m2r = root.worldToLocalMatrix * l2w };
                p.objName = "FBX2NPR_" + list.Count + "_" + Sanitize(r.name);
                var v = mesh.vertices; var t = mesh.triangles;
                p.weld = new int[v.Length]; for (int i = 0; i < v.Length; i++) p.weld[i] = -1;
                var byPos = new Dictionary<Vector3, int>();   // 같은 자리 = 같은 값 (UV 이음새에서 갈라진 정점은 원래 한 점이라 좌표가 똑같다)
                for (int i = 0; i < t.Length; i += 3)
                {
                    if (w[t[i]].x <= 0f || w[t[i + 1]].x <= 0f || w[t[i + 2]].x <= 0f) continue;
                    for (int j = 0; j < 3; j++)
                    {
                        int k = t[i + j]; p.tris.Add(k);
                        if (p.weld[k] >= 0) continue;
                        var q = p.m2r.MultiplyPoint3x4(v[k]); int id;
                        if (!byPos.TryGetValue(q, out id)) { id = p.weldPos.Count; byPos[q] = id; p.weldPos.Add(q); }
                        p.weld[k] = id; p.local[k] = p.used.Count; p.used.Add(k);
                    }
                }
                if (p.tris.Count > 0) list.Add(p);
            }
            return list;
        }

        /// <summary>얼굴 영역을 OBJ 로 쓴다. 쓴 내용 한 줄, 내보낼 얼굴이 없으면 null.</summary>
        public static string Export(ToonFaceRig rig, string objPath)
        {
            var pieces = Pieces(rig);
            if (pieces.Count == 0) return null;
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("# FBX2NPR face normals. In Blender edit normals only, then export OBJ with normals on and default axes/scale.\n");
            sb.Append("# Do not add, delete or merge vertices. model ").Append(rig.sourceModelGuid).Append('\n');
            int vBase = 0, tBase = 0, tris = 0, verts = 0;
            foreach (var p in pieces)
            {
                sb.Append("o ").Append(p.objName).Append('\n');
                foreach (var q in p.weldPos) sb.AppendFormat(ci, "v {0:F6} {1:F6} {2:F6}\n", -q.x, q.y, q.z);   // 왼손(유니티) → 오른손(OBJ): x 를 뒤집는다
                var uv = new List<Vector2>(); p.mesh.GetUVs(0, uv);
                var nrm = p.mesh.normals; var nm = p.m2r.inverse.transpose;
                foreach (int k in p.used) { var t = uv.Count == p.mesh.vertexCount ? uv[k] : Vector2.zero; sb.AppendFormat(ci, "vt {0:F6} {1:F6}\n", t.x, t.y); }
                foreach (int k in p.used) { var n = nm.MultiplyVector(nrm[k]).normalized; sb.AppendFormat(ci, "vn {0:F6} {1:F6} {2:F6}\n", -n.x, n.y, n.z); }
                for (int i = 0; i < p.tris.Count; i += 3)   // x 를 뒤집었으니 감김도 뒤집는다 (a, c, b)
                    sb.Append("f ").Append(Corner(p, p.tris[i], vBase, tBase)).Append(' ').Append(Corner(p, p.tris[i + 2], vBase, tBase)).Append(' ').Append(Corner(p, p.tris[i + 1], vBase, tBase)).Append('\n');
                vBase += p.weldPos.Count; tBase += p.used.Count; tris += p.tris.Count / 3; verts += p.weldPos.Count;
            }
            string dir = Path.GetDirectoryName(objPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(objPath, sb.ToString(), new UTF8Encoding(false));
            return string.Format(ci, "얼굴 메시 {0}개, 정점 {1}개, 삼각형 {2}개 → {3}", pieces.Count, verts, tris, objPath);
        }

        static string Corner(Piece p, int k, int vBase, int tBase)
        {
            int v = vBase + p.weld[k] + 1, t = tBase + p.local[k] + 1;
            return v + "/" + t + "/" + t;
        }

        static float F(string s) { return float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }
        static int Idx(string s, int count) { int i = int.Parse(s, CultureInfo.InvariantCulture); return i > 0 ? i - 1 : count + i; }
        static Vector3Int Cell(Vector3 q) { return new Vector3Int(Mathf.FloorToInt(q.x / GridSize), Mathf.FloorToInt(q.y / GridSize), Mathf.FloorToInt(q.z / GridSize)); }

        static int Find(Dictionary<Vector3Int, List<int>> grid, List<Vector3> pos, Vector3 q)
        {
            var c = Cell(q); int best = -1; float bd = GridSize * GridSize;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
            {
                List<int> ids; if (!grid.TryGetValue(new Vector3Int(c.x + dx, c.y + dy, c.z + dz), out ids)) continue;
                foreach (int id in ids) { float d = (pos[id] - q).sqrMagnitude; if (d <= bd) { bd = d; best = id; } }
            }
            return best;
        }

        static long TriKey(int a, int b, int c)
        {
            if (a > b) { int s = a; a = b; b = s; } if (b > c) { int s = b; b = c; c = s; } if (a > b) { int s = a; a = b; b = s; }
            return ((long)a * 2097152L + b) * 2097152L + c;   // 용접 번호 < 2^21
        }

        /// <summary>블렌더에서 내보낸 OBJ 를 읽어 바뀐 노멀(메시 좌표)을 돌려준다. 저장은 하지 않는다 (Save).</summary>
        public static ReadResult Read(ToonFaceRig rig, string objPath)
        {
            var res = new ReadResult();
            var pieces = Pieces(rig);
            if (pieces.Count == 0) { res.error = "얼굴 메시를 찾지 못했습니다 (변환한 프리팹의 툰 얼굴 리그인지 확인)"; return res; }

            // OBJ 의 번호는 파일 전체로 센다 (객체마다 새로 세지 않는다). 좌표는 읽으면서 왼손(유니티)으로 되돌린다.
            var V = new List<Vector3>(); var N = new List<Vector3>();
            var objNames = new List<string>(); var objFaces = new List<List<int[]>>();   // 면 = 모서리마다 (v, vn)
            List<int[]> cur = null;
            foreach (var raw in File.ReadAllLines(objPath))
            {
                var line = raw.Trim(); if (line.Length == 0 || line[0] == '#') continue;
                var sp = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (sp[0] == "o") { objNames.Add(sp.Length > 1 ? sp[1] : ""); cur = new List<int[]>(); objFaces.Add(cur); }
                else if (sp[0] == "v") V.Add(new Vector3(-F(sp[1]), F(sp[2]), F(sp[3])));
                else if (sp[0] == "vn") N.Add(new Vector3(-F(sp[1]), F(sp[2]), F(sp[3])));
                else if (sp[0] == "f")
                {
                    if (cur == null) { objNames.Add(""); cur = new List<int[]>(); objFaces.Add(cur); }
                    var c = new int[(sp.Length - 1) * 2];
                    for (int j = 1; j < sp.Length; j++)
                    {
                        var part = sp[j].Split('/');
                        c[(j - 1) * 2] = Idx(part[0], V.Count);
                        c[(j - 1) * 2 + 1] = part.Length > 2 && part[2].Length > 0 ? Idx(part[2], N.Count) : -1;
                    }
                    cur.Add(c);
                }
            }
            if (N.Count == 0) { res.error = "OBJ 에 노멀(vn)이 없습니다. 블렌더에서 내보낼 때 '노멀' 항목을 켜 주세요"; return res; }

            var seen = new HashSet<Piece>();
            int missV = 0, totalV = 0;
            for (int o = 0; o < objNames.Count; o++)
            {
                Piece p = null;
                foreach (var q in pieces) if (objNames[o] == q.objName || objNames[o].StartsWith(q.objName + ".", StringComparison.Ordinal)) { p = q; break; }
                if (p == null) { if (objFaces[o].Count > 0) res.lines.Add("보낸 적 없는 객체는 건너뜀: " + objNames[o]); continue; }
                if (!seen.Add(p)) { res.lines.Add("같은 메시가 두 번 들어 있어 뒤의 것은 건너뜀: " + objNames[o]); continue; }

                var grid = new Dictionary<Vector3Int, List<int>>();
                for (int id = 0; id < p.weldPos.Count; id++)
                {
                    var key = Cell(p.weldPos[id]); List<int> ids;
                    if (!grid.TryGetValue(key, out ids)) grid[key] = ids = new List<int>();
                    ids.Add(id);
                }
                var byTri = new Dictionary<long, int>();   // 용접 번호 셋 → 내보낸 삼각형 (p.tris 의 시작 위치)
                for (int i = 0; i < p.tris.Count; i += 3) { long k = TriKey(p.weld[p.tris[i]], p.weld[p.tris[i + 1]], p.weld[p.tris[i + 2]]); if (!byTri.ContainsKey(k)) byTri[k] = i; }

                var weldOf = new Dictionary<int, int>();   // OBJ 정점 번호 → 용접 번호 (-1 = 위치가 안 맞음)
                var sum = new Dictionary<int, Vector3>(); var first = new Dictionary<int, Vector3>(); var split = new HashSet<int>();
                var toMesh = p.m2r.transpose;   // 루트 좌표 노멀 → 메시 좌표 (내보낼 때 쓴 역전치의 역)
                int badFaces = 0;
                foreach (var f in objFaces[o])
                {
                    int nc = f.Length / 2;
                    if (nc != 3) { badFaces++; continue; }
                    var wid = new int[3];
                    for (int j = 0; j < 3; j++)
                    {
                        int vi = f[j * 2]; int id;
                        if (!weldOf.TryGetValue(vi, out id)) { id = vi >= 0 && vi < V.Count ? Find(grid, p.weldPos, V[vi]) : -1; weldOf[vi] = id; totalV++; if (id < 0) missV++; }
                        wid[j] = id;
                    }
                    int start;
                    if (wid[0] < 0 || wid[1] < 0 || wid[2] < 0 || !byTri.TryGetValue(TriKey(wid[0], wid[1], wid[2]), out start)) { badFaces++; continue; }
                    for (int j = 0; j < 3; j++)
                    {
                        int ni = f[j * 2 + 1]; if (ni < 0 || ni >= N.Count) continue;
                        int k = -1;
                        for (int m = 0; m < 3; m++) if (p.weld[p.tris[start + m]] == wid[j]) { k = p.tris[start + m]; break; }
                        if (k < 0) continue;
                        var n = toMesh.MultiplyVector(N[ni]).normalized;
                        Vector3 s0;
                        if (sum.TryGetValue(k, out s0)) { sum[k] = s0 + n; if (Vector3.Angle(first[k], n) > SplitDeg) split.Add(k); }
                        else { sum[k] = n; first[k] = n; }
                    }
                }

                var cur0 = p.mesh.normals; var idx = new List<int>(); var nn = new List<Vector3>(); float maxDeg = 0f;
                foreach (var kv in sum)
                {
                    var n = kv.Value.normalized; float deg = Vector3.Angle(cur0[kv.Key], n);
                    if (deg <= ChangedDeg) continue;
                    idx.Add(kv.Key); nn.Add(n); maxDeg = Mathf.Max(maxDeg, deg);
                }
                if (idx.Count > 0) res.entries.Add(new FaceNormalEdit.Entry { mesh = p.mesh.name, vertexCount = p.mesh.vertexCount, indices = idx.ToArray(), normals = nn.ToArray() });
                res.lines.Add(string.Format(CultureInfo.InvariantCulture, "{0}: 돌아온 면 {1}개 (짝 못 찾음 {2}), 노멀을 받은 정점 {3}/{4}개, 바뀐 정점 {5}개 (최대 {6:F1}°)",
                    p.name, objFaces[o].Count, badFaces, sum.Count, p.used.Count, idx.Count, maxDeg));
                if (split.Count > 0) res.lines.Add("  " + split.Count + "개 정점은 모서리마다 노멀이 달라(블렌더의 날카로운 모서리) 평균을 넣었습니다 — 유니티 정점에는 노멀이 하나뿐입니다");
            }
            foreach (var q in pieces) if (!seen.Contains(q)) res.lines.Add("보낸 메시가 돌아온 파일에 없음: " + q.name + " (" + q.objName + ")");
            if (seen.Count == 0) { res.error = "보낸 얼굴 메시를 하나도 찾지 못했습니다. 이 리그에서 내보낸 OBJ 인지, 블렌더에서 객체를 합치거나 이름을 바꾸지 않았는지 확인하세요"; return res; }
            if (totalV > 0 && missV > totalV * MaxMissFrac)
            {
                res.error = string.Format(CultureInfo.InvariantCulture, "정점 위치가 보낸 것과 맞지 않습니다 ({0}/{1}개). 블렌더에서 OBJ 를 내보낼 때 축(앞 -Z, 위 Y)과 크기(1.0)를 기본값으로 두었는지, 정점을 더하거나 지우지 않았는지 확인하세요", missV, totalV);
                res.entries.Clear();
            }
            return res;
        }

        /// <summary>불러온 노멀을 원본 모델 옆 FaceNormalEdit 에 합쳐 저장한다.</summary>
        public static FaceNormalEdit Save(string modelPath, List<FaceNormalEdit.Entry> entries)
        {
            string path = FaceNormalEdit.PathFor(modelPath);
            var asset = AssetDatabase.LoadAssetAtPath<FaceNormalEdit>(path);
            if (asset == null) { asset = ScriptableObject.CreateInstance<FaceNormalEdit>(); asset.Merge(entries); AssetDatabase.CreateAsset(asset, path); }
            else { asset.Merge(entries); EditorUtility.SetDirty(asset); }
            AssetDatabase.SaveAssets();
            return asset;
        }

        /// <summary>저장한 노멀을 지운다 (다음 변환부터 자동 노멀). 지웠으면 true.</summary>
        public static bool Clear(string modelPath)
        {
            return AssetDatabase.LoadAssetAtPath<FaceNormalEdit>(FaceNormalEdit.PathFor(modelPath)) != null && AssetDatabase.DeleteAsset(FaceNormalEdit.PathFor(modelPath));
        }
    }
}
