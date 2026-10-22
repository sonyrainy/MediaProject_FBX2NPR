using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace FBX2NPR
{
    /// <summary>
    /// 깊이·노멀 버퍼에 소벨을 걸어 화면 공간 외곽선을 그립니다.
    ///
    /// 메시 띠 방식을 대체합니다. 띠는 월드 공간이라 두께가 거리와 각도에 따라 변했고,
    /// 고정색이라 그늘에서 배경보다 밝아져 선이 아니라 긁힌 자국으로 보였습니다.
    /// 화면 공간 방식은 두께가 픽셀 단위로 일정하고, 결과를 곱하므로 항상 어두워집니다.
    /// </summary>
    [DisallowMultipleRendererFeature("FBX2NPR Sobel Outline")]
    public class SobelOutlineFeature : ScriptableRendererFeature
    {
        [Serializable]
        public class Settings
        {
            [Range(0f, 1f)]  public float darkness    = 0.35f;
            [Range(0.5f, 4f)] public float thickness  = 1.0f;
            [Range(0f, 40f)] public float depthScale  = 12f;
            [Range(0f, 8f)]  public float normalScale = 0.8f;   // 훑기 결과. 2.2 는 포화돼 선이 뭉갰습니다.
            [Range(0f, 1f)]  public float threshold   = 0.30f;
            public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingTransparents;
            // 0=정상, 1=엣지만, 2=씬 노멀, 3=깊이
            public float debug = 0f;
        }

        public Settings settings = new Settings();
        [Tooltip("외곽선 셰이더 에셋. 비어 있으면 Shader.Find 로 찾는다 — 빌드에서는 아무도 참조하지 않는 셰이더가 빠지므로 변환 도구가 채운다.")] public Shader shader;

        // 검증용 계수기. 패스가 정말 돌았는지 밖에서 확인합니다.
        public static int s_AddCalls, s_RecordCalls, s_ShaderNull, s_DepthValid, s_NormalsValid;

        Material m_Material;
        SobelPass m_Pass;

        public override void Create()
        {
            m_Pass = new SobelPass();
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            s_AddCalls++;
            if (m_Material == null)
            {
                var sh = shader != null ? shader : Shader.Find("FBX2NPR/SobelOutline");
                if (sh == null) { s_ShaderNull++; return; }  // 셰이더가 없으면 조용히 건너뜁니다
                m_Material = CoreUtils.CreateEngineMaterial(sh);
            }
            m_Pass.renderPassEvent = settings.injectionPoint;
            m_Pass.Setup(m_Material, settings);
            // 이 선언이 있어야 URP 가 깊이·노멀 버퍼를 만들어 줍니다.
            m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }

        class SobelPass : ScriptableRenderPass
        {
            Material m_Mat;

            static readonly int s_Darkness    = Shader.PropertyToID("_Darkness");
            static readonly int s_Thickness   = Shader.PropertyToID("_Thickness");
            static readonly int s_DepthScale  = Shader.PropertyToID("_DepthScale");
            static readonly int s_NormalScale = Shader.PropertyToID("_NormalScale");
            static readonly int s_Threshold   = Shader.PropertyToID("_Threshold");
            static readonly int s_Debug       = Shader.PropertyToID("_Debug");

            public void Setup(Material mat, Settings s)
            {
                m_Mat = mat;
                if (m_Mat == null) return;
                m_Mat.SetFloat(s_Darkness,    s.darkness);
                m_Mat.SetFloat(s_Thickness,   s.thickness);
                m_Mat.SetFloat(s_DepthScale,  s.depthScale);
                m_Mat.SetFloat(s_NormalScale, s.normalScale);
                m_Mat.SetFloat(s_Threshold,   s.threshold);
                m_Mat.SetFloat(s_Debug,       s.debug);
            }

            class PassData
            {
                public Material mat;
                public TextureHandle src;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                if (m_Mat == null) return;
                s_RecordCalls++;
                var res = frameData.Get<UniversalResourceData>();
                var cam = frameData.Get<UniversalCameraData>();
                if (cam.isPreviewCamera) return;
                if (!res.activeColorTexture.IsValid()) return;
                s_DepthValid   = res.cameraDepthTexture.IsValid()   ? 1 : 0;
                s_NormalsValid = res.cameraNormalsTexture.IsValid() ? 1 : 0;

                // 자기 자신을 읽으면서 쓸 수 없으므로 색을 한 번 복사합니다.
                var desc = renderGraph.GetTextureDesc(res.cameraColor);
                desc.name = "_FBX2NPRSobelCopy";
                desc.clearBuffer = false;
                desc.depthBufferBits = 0;
                desc.msaaSamples = MSAASamples.None;
                var copy = renderGraph.CreateTexture(desc);

                // URP 버전마다 AddBlitPass 확장이 사는 네임스페이스가 달라서,
                // 복사도 직접 래스터 패스로 씁니다. 외부 의존성이 하나 줄어듭니다.
                using (var cb = renderGraph.AddRasterRenderPass<PassData>("FBX2NPR Sobel Copy", out var cd))
                {
                    cd.mat = null;
                    cd.src = res.activeColorTexture;
                    cb.UseTexture(res.activeColorTexture, AccessFlags.Read);
                    cb.SetRenderAttachment(copy, 0, AccessFlags.Write);
                    cb.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, d.src, new Vector4(1f, 1f, 0f, 0f), 0f, false);
                    });
                }

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("FBX2NPR Sobel Outline", out var pd))
                {
                    pd.mat = m_Mat;
                    pd.src = copy;
                    builder.UseTexture(copy, AccessFlags.Read);
                    if (res.cameraDepthTexture.IsValid())   builder.UseTexture(res.cameraDepthTexture);
                    if (res.cameraNormalsTexture.IsValid()) builder.UseTexture(res.cameraNormalsTexture);
                    builder.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        Blitter.BlitTexture(ctx.cmd, d.src, new Vector4(1f, 1f, 0f, 0f), d.mat, 0);
                    });
                }
            }
        }
    }
}