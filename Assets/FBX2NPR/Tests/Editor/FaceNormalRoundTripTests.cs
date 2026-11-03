using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FBX2NPR.EditorTools;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FBX2NPR.Tests
{
    /// <summary>
    /// 블렌더 노멀 왕복(FaceNormalRoundTrip)을 지킵니다. 블렌더 없이, 내보낸 OBJ 글자를 블렌더가 하듯 고쳐서 확인합니다.
    /// - 손대지 않고 돌아오면 바뀐 정점이 없어야 합니다 (손대지 않은 노멀은 그대로 남게).
    /// - 한쪽만 고치면 그쪽 정점만 고친 만큼 바뀌어야 합니다. 메시가 루트와 다른 자리·회전에 있어도 같아야 합니다.
    /// - 축이 바뀐 파일은 불러오지 않아야 합니다.
    /// - 저장한 노멀은 정점 수가 같을 때만 들어가고, 새로 불러온 값이 같은 정점의 예전 값을 이기고, 원본 노멀은 새 노멀 기준으로 다시 구워져야 합니다.
    /// </summary>
    public class FaceNormalRoundTripTests
    {
        GameObject root; Mesh mesh; ToonFaceRig rig; string dir;

        // 반구 캡: 고리 8개 × 24칸. 이음새(u = 0 / 1)는 유니티처럼 같은 자리에 정점을 따로 두고, 가장 바깥 고리는 얼굴 가중치 0 (내보내지 않는 영역)
        static Mesh Cap()
        {
            const int NR = 8, NS = 24; const float R = 0.1f;
            var v = new List<Vector3> { new Vector3(0f, 0f, R) }; var uv = new List<Vector2> { new Vector2(0.5f, 1f) }; var w = new List<Vector2> { new Vector2(1f, 0f) };
            for (int i = 1; i <= NR; i++)
            {
                float th = Mathf.Deg2Rad * 60f * i / NR;
                for (int s = 0; s <= NS; s++)
                {
                    float ph = 2f * Mathf.PI * (s % NS) / NS;
                    v.Add(new Vector3(R * Mathf.Sin(th) * Mathf.Cos(ph), R * Mathf.Sin(th) * Mathf.Sin(ph), R * Mathf.Cos(th)));
                    uv.Add(new Vector2((float)s / NS, 1f - (float)i / NR));
                    w.Add(new Vector2(i < NR ? 1f : 0f, 0f));
                }
            }
            Func<int, int, int> at = (i, s) => 1 + (i - 1) * (NS + 1) + s;
            var t = new List<int>();
            for (int s = 0; s < NS; s++) { t.Add(0); t.Add(at(1, s + 1)); t.Add(at(1, s)); }
            for (int i = 1; i < NR; i++)
                for (int s = 0; s < NS; s++)
                {
                    int a = at(i, s), b = at(i, s + 1), c = at(i + 1, s + 1), d = at(i + 1, s);
                    t.Add(a); t.Add(c); t.Add(d); t.Add(a); t.Add(b); t.Add(c);
                }
            var m = new Mesh { name = "RT_Face" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetUVs(2, w); m.SetTriangles(t, 0);
            var n = new List<Vector3>(); foreach (var p in v) n.Add(p.normalized);
            m.SetNormals(n); m.RecalculateTangents();
            return m;
        }

        [SetUp]
        public void Make()
        {
            root = new GameObject("RT model");
            var face = new GameObject("Face"); face.transform.SetParent(root.transform, false);
            face.transform.localPosition = new Vector3(0.1f, 1.5f, 0.02f); face.transform.localRotation = Quaternion.Euler(10f, 30f, 0f);   // 루트와 다른 자리·회전
            mesh = Cap();
            face.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = face.AddComponent<MeshRenderer>();
            var rgo = new GameObject("rig"); rgo.SetActive(false); rgo.transform.SetParent(root.transform, false);   // 꺼 두어 OnEnable(Apply) 이 돌지 않게
            rig = rgo.AddComponent<ToonFaceRig>(); rig.faceRenderers = new List<Renderer> { mr };
            dir = Path.Combine(Path.GetTempPath(), "fbx2npr_rt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void Clean() { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        string Exported()
        {
            string p = Path.Combine(dir, "face.obj");
            Assert.That(FaceNormalRoundTrip.Export(rig, p), Is.Not.Null, "내보낼 얼굴이 없습니다");
            return p;
        }

        // 블렌더가 하듯 OBJ 글자를 고친다: 위치(OBJ 좌표)가 pick 을 만족하는 정점의 모서리 노멀을 OBJ 의 Y 축으로 deg 만큼 돌린다
        static void RotateNormals(string path, Func<Vector3, bool> pick, float deg)
        {
            var ci = CultureInfo.InvariantCulture; var lines = File.ReadAllLines(path);
            var v = new List<Vector3>(); var vnLine = new List<int>(); var vOfVn = new Dictionary<int, int>();
            for (int i = 0; i < lines.Length; i++)
            {
                var sp = lines[i].Split(' ');
                if (sp[0] == "v") v.Add(new Vector3(float.Parse(sp[1], ci), float.Parse(sp[2], ci), float.Parse(sp[3], ci)));
                else if (sp[0] == "vn") vnLine.Add(i);
                else if (sp[0] == "f") for (int j = 1; j < sp.Length; j++) { var c = sp[j].Split('/'); vOfVn[int.Parse(c[2], ci) - 1] = int.Parse(c[0], ci) - 1; }
            }
            var rot = Quaternion.AngleAxis(deg, Vector3.up);
            for (int k = 0; k < vnLine.Count; k++)
            {
                int vi; if (!vOfVn.TryGetValue(k, out vi) || !pick(v[vi])) continue;
                var sp = lines[vnLine[k]].Split(' ');
                var n = rot * new Vector3(float.Parse(sp[1], ci), float.Parse(sp[2], ci), float.Parse(sp[3], ci));
                lines[vnLine[k]] = string.Format(ci, "vn {0:F6} {1:F6} {2:F6}", n.x, n.y, n.z);
            }
            File.WriteAllLines(path, lines);
        }

        [Test]
        public void UntouchedRoundTripChangesNothing()
        {
            var res = FaceNormalRoundTrip.Read(rig, Exported());
            Assert.That(res.error, Is.Null, string.Join("\n", res.lines));
            Assert.That(res.entries, Is.Empty, "손대지 않았는데 바뀐 정점이 있습니다");
        }

        [Test]
        public void EditedNormalsComeBackOnlyWhereEdited()
        {
            const float Deg = 20f, Side = -0.07f;   // 메시가 루트 x = 0.1 에 있어 OBJ x 는 약 -0.19 ~ -0.01. 그 가운데 한쪽(약 3분의 1)만 고친다
            string p = Exported();
            RotateNormals(p, q => q.x > Side, Deg);
            var res = FaceNormalRoundTrip.Read(rig, p);
            Assert.That(res.error, Is.Null, string.Join("\n", res.lines));
            Assert.That(res.entries.Count, Is.EqualTo(1), string.Join("\n", res.lines));

            var e = res.entries[0]; var got = new Dictionary<int, Vector3>();
            for (int i = 0; i < e.indices.Length; i++) got[e.indices[i]] = e.normals[i];
            var m2r = root.transform.worldToLocalMatrix * rig.faceRenderers[0].transform.localToWorldMatrix;
            var toRoot = m2r.inverse.transpose; var toMesh = m2r.transpose; var rot = Quaternion.AngleAxis(Deg, Vector3.up);
            var verts = mesh.vertices; var nrm = mesh.normals; var w = new List<Vector2>(); mesh.GetUVs(2, w);
            int edited = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                bool inEdit = w[i].x > 0f && -m2r.MultiplyPoint3x4(verts[i]).x > Side;   // OBJ x = -(루트 x)
                Assert.That(got.ContainsKey(i), Is.EqualTo(inEdit), "정점 " + i + (inEdit ? ": 고친 쪽인데 바뀌지 않았습니다" : ": 고치지 않은 쪽인데 바뀌었습니다"));
                if (!inEdit) continue;
                var r = toRoot.MultiplyVector(nrm[i]).normalized;
                var o = rot * new Vector3(-r.x, r.y, r.z);
                var want = toMesh.MultiplyVector(new Vector3(-o.x, o.y, o.z)).normalized;
                Assert.That(Vector3.Angle(got[i], want), Is.LessThan(0.05f), "정점 " + i + ": 고친 만큼 바뀌지 않았습니다");
                edited++;
            }
            Assert.That(edited, Is.GreaterThan(0));
        }

        [Test]
        public void ChangedAxesAreRefused()
        {
            string p = Exported(); var ci = CultureInfo.InvariantCulture;
            var lines = File.ReadAllLines(p);
            for (int i = 0; i < lines.Length; i++)
            {
                var sp = lines[i].Split(' ');
                if (sp[0] == "v") lines[i] = "v " + sp[1] + " " + sp[3] + " " + (-float.Parse(sp[2], ci)).ToString("F6", ci);   // Z 를 위로 내보낸 것처럼
            }
            File.WriteAllLines(p, lines);
            var res = FaceNormalRoundTrip.Read(rig, p);
            Assert.That(res.error, Is.Not.Null, "축이 바뀐 파일을 불러왔습니다");
            Assert.That(res.entries, Is.Empty);
        }

        [Test]
        public void SavedNormalsGoInOnlyWhenTheMeshMatches()
        {
            var edit = ScriptableObject.CreateInstance<FaceNormalEdit>();
            try
            {
                edit.entries.Add(new FaceNormalEdit.Entry { mesh = mesh.name, vertexCount = mesh.vertexCount, indices = new[] { 1, 2 }, normals = new[] { Vector3.up, Vector3.right } });
                Assert.That(edit.ApplyTo(mesh), Does.Contain("적용"));
                Assert.That(Vector3.Angle(mesh.normals[1], Vector3.up), Is.LessThan(0.01f));
                Assert.That(Vector3.Angle(mesh.normals[2], Vector3.right), Is.LessThan(0.01f));
                Assert.That(mesh.tangents.Length, Is.EqualTo(mesh.vertexCount));

                var before = mesh.normals;
                edit.entries[0].vertexCount = mesh.vertexCount + 1;   // 원본 모델이 바뀐 경우
                edit.entries[0].normals = new[] { Vector3.forward, Vector3.forward };
                Assert.That(edit.ApplyTo(mesh), Does.Contain("정점 수"));
                Assert.That(mesh.normals, Is.EqualTo(before));
            }
            finally { Object.DestroyImmediate(edit); }
        }

        [Test]
        public void NewerEditsWinOnTheSameVertices()
        {
            var edit = ScriptableObject.CreateInstance<FaceNormalEdit>();
            try
            {
                edit.Merge(new[] { new FaceNormalEdit.Entry { mesh = "M", vertexCount = 10, indices = new[] { 1, 3 }, normals = new[] { Vector3.up, Vector3.up } } });
                edit.Merge(new[] { new FaceNormalEdit.Entry { mesh = "M", vertexCount = 10, indices = new[] { 3, 5 }, normals = new[] { Vector3.right, Vector3.right } } });
                Assert.That(edit.entries.Count, Is.EqualTo(1));
                Assert.That(edit.entries[0].indices, Is.EqualTo(new[] { 1, 3, 5 }));
                Assert.That(edit.entries[0].normals, Is.EqualTo(new[] { Vector3.up, Vector3.right, Vector3.right }));
            }
            finally { Object.DestroyImmediate(edit); }
        }

        [Test]
        public void OriginalNormalsAreRebakedForTheEditedNormals()
        {
            var orig = mesh.normals;   // 이 시험에서는 지금 노멀을 원본 노멀로 본다
            var edit = ScriptableObject.CreateInstance<FaceNormalEdit>();
            try
            {
                var idx = new[] { 5, 6, 7 }; var nn = new Vector3[idx.Length];
                for (int i = 0; i < idx.Length; i++) nn[i] = Quaternion.AngleAxis(25f, Vector3.up) * orig[idx[i]];
                edit.entries.Add(new FaceNormalEdit.Entry { mesh = mesh.name, vertexCount = mesh.vertexCount, indices = idx, normals = nn });
                edit.ApplyTo(mesh);
                OriginalNormals.Write(mesh, orig);   // 변환기와 같은 순서: 노멀을 바꾼 뒤 원본 노멀을 새 노멀 기준으로 굽는다
                var ts = new List<Vector3>(); mesh.GetUVs(OriginalNormals.Channel, ts);
                var n = mesh.normals; var t = mesh.tangents;
                for (int i = 0; i < n.Length; i++)
                    Assert.That(Vector3.Angle(OriginalNormals.Decode(ts[i], n[i], t[i]), orig[i]), Is.LessThan(0.05f), "정점 " + i + ": UV4 를 되돌린 원본 노멀이 다릅니다");
            }
            finally { Object.DestroyImmediate(edit); }
        }
    }
}
