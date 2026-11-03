using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FBX2NPR.EditorTools
{
    /// <summary>
    /// 블렌더에서 다듬어 불러온 얼굴 노멀 (FaceNormalRoundTrip). 원본 모델 옆(모델이름_FaceNormals.asset)에 두어 모델을 따라다니고,
    /// 변환기가 방사형 노멀을 만든 뒤 이 노멀로 바꿔 넣는다. 그래서 평탄화 같은 설정을 바꿔 다시 변환해도 다듬은 노멀이 남는다.
    /// 메시 이름과 정점 수로 짝짓는다. 원본 모델이 바뀌어 정점 수가 다르면 넣지 않고 변환 기록에 남긴다.
    /// </summary>
    public class FaceNormalEdit : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string mesh;
            public int vertexCount;
            public int[] indices = new int[0];        // 노멀을 바꿀 정점 (메시의 정점 번호)
            public Vector3[] normals = new Vector3[0]; // 메시 좌표 노멀, indices 와 같은 길이
        }

        public List<Entry> entries = new List<Entry>();

        public int VertexTotal { get { int n = 0; foreach (var e in entries) n += e.indices.Length; return n; } }

        public static string PathFor(string modelPath)
        {
            string dir = Path.GetDirectoryName(modelPath).Replace('\\', '/');
            return dir + "/" + Path.GetFileNameWithoutExtension(modelPath) + "_FaceNormals.asset";
        }

        public static FaceNormalEdit LoadFor(string modelPath)
        {
            return string.IsNullOrEmpty(modelPath) ? null : AssetDatabase.LoadAssetAtPath<FaceNormalEdit>(PathFor(modelPath));
        }

        /// <summary>이 메시의 노멀이 있으면 넣고 탄젠트를 다시 계산한다. 변환 기록에 남길 한 줄, 해당 없으면 null.</summary>
        public string ApplyTo(Mesh mesh)
        {
            foreach (var e in entries)
            {
                if (e.mesh != mesh.name) continue;
                if (e.vertexCount != mesh.vertexCount)
                    return "블렌더에서 다듬은 노멀을 넣지 않음: " + mesh.name + " 의 정점 수가 달라짐 (저장 " + e.vertexCount + ", 지금 " + mesh.vertexCount + ") — 원본 모델이 바뀌었으면 다시 보내서 다듬어야 합니다";
                var n = mesh.normals;
                for (int i = 0; i < e.indices.Length; i++) n[e.indices[i]] = e.normals[i];
                mesh.normals = n;
                mesh.RecalculateTangents();   // 새 노멀에 맞는 탄젠트 (원본 노멀을 UV4 에 구울 축)
                return "블렌더에서 다듬은 노멀 적용: " + mesh.name + " 정점 " + e.indices.Length + "개 (" + name + ")";
            }
            return null;
        }

        /// <summary>새로 불러온 노멀을 합친다. 같은 메시는 정점별로 새 값이 이기고, 정점 수가 바뀐 메시는 새 것으로 바꾼다.</summary>
        public void Merge(IEnumerable<Entry> incoming)
        {
            foreach (var e in incoming)
            {
                var old = entries.Find(x => x.mesh == e.mesh);
                if (old == null || old.vertexCount != e.vertexCount) { entries.Remove(old); entries.Add(e); continue; }
                var map = new SortedDictionary<int, Vector3>();
                for (int i = 0; i < old.indices.Length; i++) map[old.indices[i]] = old.normals[i];
                for (int i = 0; i < e.indices.Length; i++) map[e.indices[i]] = e.normals[i];
                old.indices = new int[map.Count]; old.normals = new Vector3[map.Count];
                int k = 0; foreach (var kv in map) { old.indices[k] = kv.Key; old.normals[k] = kv.Value; k++; }
            }
        }
    }
}
