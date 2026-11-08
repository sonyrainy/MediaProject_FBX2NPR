// FaceParser.cs — 얼굴 파싱 모델(yakhyo/face-parsing, BiSeNet ResNet18, CelebAMask-HQ 19 클래스)을 Inference Engine 으로 돌리는 얇은 래퍼.
// 입력: 512×512 sRGB RGBA 바이트(행 0 = 위). 출력: 클래스별 로짓 또는 확률 [19][512][512] (행 0 = 위).
// 모델 파일은 Assets/FBX2NPR/Models/resnet18.onnx. 라이선스: 코드 MIT, 학습 데이터(CelebAMask-HQ)는 비상업 연구 전용 — 배포물에 넣지 말 것.
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Unity.InferenceEngine;

namespace FBX2NPR.EditorTools
{
    public class FaceParser : IDisposable
    {
        public const string ModelPath = "Assets/FBX2NPR/Models/resnet18.onnx";
        public const int N = 512;                 // 입력 한 변
        public const int K = 19;                  // 클래스 수
        // 순서는 yakhyo 저장소 utils/prepare_labels.py 의 enumerate(attributes, 1). CelebAMask-HQ 원본 g_mask.py 와 다르다.
        public static readonly string[] Names = { "bg", "skin", "l_brow", "r_brow", "l_eye", "r_eye", "eye_g", "l_ear", "r_ear", "ear_r", "nose", "mouth", "u_lip", "l_lip", "neck", "neck_l", "cloth", "hair", "hat" };
        public const int Bg = 0, Skin = 1, LBrow = 2, RBrow = 3, LEye = 4, REye = 5, EyeG = 6, LEar = 7, REar = 8, EarR = 9, Nose = 10, Mouth = 11, ULip = 12, LLip = 13, Neck = 14, NeckL = 15, Cloth = 16, Hair = 17, Hat = 18;
        public static readonly Color32[] Palette = {
            new Color32(0, 0, 0, 255), new Color32(230, 180, 150, 255), new Color32(60, 40, 20, 255), new Color32(120, 80, 40, 255),
            new Color32(0, 120, 255, 255), new Color32(0, 200, 255, 255), new Color32(200, 200, 200, 255), new Color32(0, 180, 60, 255),
            new Color32(120, 230, 60, 255), new Color32(255, 255, 0, 255), new Color32(255, 120, 0, 255), new Color32(255, 0, 120, 255),
            new Color32(220, 0, 0, 255), new Color32(150, 0, 60, 255), new Color32(160, 120, 200, 255), new Color32(255, 0, 255, 255),
            new Color32(80, 80, 160, 255), new Color32(90, 50, 30, 255), new Color32(255, 255, 255, 255),
        };
        static readonly float[] Mean = { 0.485f, 0.456f, 0.406f }, Std = { 0.229f, 0.224f, 0.225f };   // inference.py 의 ImageNet 정규화

        Worker worker;
        public BackendType backend;
        public string note;                       // 로드 결과 요약 (로그용)

        /// <summary>모델을 읽고 워커를 만든다. 실패하면 null 을 돌려주고 이유를 log 에 적는다.</summary>
        public static FaceParser Load(List<string> log)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(ModelPath);
            if (asset == null) { log.Add("얼굴 파싱 모델 없음: " + ModelPath + " (ONNX 임포트 실패면 유니티 로그의 OnnxImportException 을 볼 것)"); return null; }
            Model model;
            try { model = ModelLoader.Load(asset); }
            catch (Exception ex) { log.Add("얼굴 파싱 모델 로드 실패: " + ex.GetType().Name + " " + ex.Message); return null; }
            var p = new FaceParser();
            p.backend = BackendType.GPUCompute;
            try { p.worker = new Worker(model, p.backend); }
            catch (Exception ex) { log.Add("GPUCompute 워커 실패(" + ex.Message + ") → CPU"); p.backend = BackendType.CPU; p.worker = new Worker(model, p.backend); }
            var sb = new StringBuilder();
            sb.Append("얼굴 파싱 모델: 입력 "); foreach (var i in model.inputs) sb.Append(i.name + " " + i.shape + " ");
            sb.Append("출력 " + model.outputs.Count + "개(첫 것 사용), 레이어 " + model.layers.Count + ", 백엔드 " + p.backend);
            p.note = sb.ToString(); log.Add(p.note);
            return p;
        }

        /// <summary>sRGB RGBA(행 0 = 위) → 로짓 [K][N][N]</summary>
        public float[] Logits(byte[] rgbaTopDown)
        {
            var data = new float[3 * N * N];
            for (int c = 0; c < 3; c++)
            {
                float m = Mean[c], s = Std[c]; int baseC = c * N * N;
                for (int i = 0; i < N * N; i++) data[baseC + i] = (rgbaTopDown[i * 4 + c] / 255f - m) / s;
            }
            using (var input = new Tensor<float>(new TensorShape(1, 3, N, N), data))
            {
                worker.Schedule(input);
                var outT = worker.PeekOutput(0) as Tensor<float>;
                if (outT == null || outT.shape.rank != 4 || outT.shape[1] != K || outT.shape[2] != N || outT.shape[3] != N)
                    throw new InvalidOperationException("얼굴 파싱 출력 모양이 (1," + K + "," + N + "," + N + ") 가 아님: " + (outT != null ? outT.shape.ToString() : "null"));
                using (var cpu = outT.ReadbackAndClone()) return cpu.DownloadToArray();
            }
        }

        /// <summary>로짓 → 확률 (같은 배열 구조). 반환값은 화소별 최대확률 평균.</summary>
        public static float Softmax(float[] logits, float[] probs)
        {
            int P = N * N; double sumMax = 0; var e = new double[K];
            for (int i = 0; i < P; i++)
            {
                float mx = float.NegativeInfinity;
                for (int k = 0; k < K; k++) { float v = logits[k * P + i]; if (v > mx) mx = v; }
                double z = 0; for (int k = 0; k < K; k++) { e[k] = Math.Exp(logits[k * P + i] - mx); z += e[k]; }
                double inv = 1.0 / z; double best = 0;
                for (int k = 0; k < K; k++) { float pv = (float)(e[k] * inv); probs[k * P + i] = pv; if (pv > best) best = pv; }
                sumMax += best;
            }
            return (float)(sumMax / P);
        }

        public void Dispose() { if (worker != null) { worker.Dispose(); worker = null; } }
    }
}
