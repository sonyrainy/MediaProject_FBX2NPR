using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
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
    ///
    /// 얼굴 렌더러(변환 도구가 OriginalNormals.LineLayer 를 켠 것)는 원본 노멀로 한 번 더 그려 그 노멀로 선을 찾습니다.
    /// 이미 평평하게 칠한 얼굴(실루엣 선만)은 카메라 방향 노멀로 그려지고, 그 얼굴 안에서는 깊이가 끊긴 곳만 선으로 봅니다(flatFaceJumpOnly).
    /// 노멀 버퍼에는 재질 셰이더가 쓴 값이 들어가는데, ToonBand 가 아닌 셰이더(UTS 등)는 정리된 노멀을 써서 코·입 선이 사라지기 때문입니다.
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
            [Tooltip("실루엣 선만 쓰는 얼굴(이목구비를 텍스처에 그린 얼굴) 안에서는 깊이가 앞뒤로 끊긴 곳만 선으로 본다. 코볼·입 옆처럼 비스듬히 돌아가는 면이 흐린 겹선(팔자주름처럼)으로 잡혀 넣었다(Night Dress). 턱 아래 목처럼 떨어진 곳과 얼굴 테두리는 그대로 그린다.")]
            public bool flatFaceJumpOnly = true;
            public RenderPassEvent injectionPoint = RenderPassEvent.AfterRenderingTransparents;
            // 0=정상, 1=엣지만, 2=씬 노멀, 3=깊이, 4=실루엣만 얼굴 안(깊이 끊김만 보는 곳)
            public float debug = 0f;
            [Tooltip("얼굴 렌더러를 원본 노멀로 다시 그려 그 노멀로 선을 찾는다. 끄면 재질 셰이더가 노멀 버퍼에 쓴 값만 쓴다.")]
            public bool restoreOriginalNormals = true;
        }

        public Settings settings = new Settings();
        [Tooltip("외곽선 셰이더 에셋. 비어 있으면 Shader.Find 로 찾는다 — 빌드에서는 아무도 참조하지 않는 셰이더가 빠지므로 변환 도구가 채운다.")] public Shader shader;
        [Tooltip("원본 노멀 패스 셰이더 에셋 (FBX2NPR/LineNormals). 외곽선 셰이더와 같은 이유로 변환 도구가 채운다.")] public Shader lineNormalsShader;

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
            var lineShader = !settings.restoreOriginalNormals ? null : lineNormalsShader != null ? lineNormalsShader : Shader.Find("FBX2NPR/LineNormals");
            m_Pass.Setup(m_Material, settings, lineShader);
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
            static readonly int s_FlatFaceJump = Shader.PropertyToID("_FlatFaceJumpOnly");
            static readonly int s_LineNormals = Shader.PropertyToID("_FBX2NPRLineNormals");
            static readonly int s_LineOn      = Shader.PropertyToID("_LineNormalsOn");
            // 원본 노멀 패스로 그릴 재질의 패스. 거의 모든 URP 셰이더(ToonBand, UTS, lilToon, Lit)에 있어 재질마다 한 번씩만 그려진다.
            static readonly ShaderTagId s_DepthOnly = new ShaderTagId("DepthOnly");

            Shader m_LineShader;

            public void Setup(Material mat, Settings s, Shader lineShader)
            {
                m_Mat = mat;
                m_LineShader = lineShader;
                if (m_Mat == null) return;
                m_Mat.SetFloat(s_Darkness,    s.darkness);
                m_Mat.SetFloat(s_Thickness,   s.thickness);
                m_Mat.SetFloat(s_DepthScale,  s.depthScale);
                m_Mat.SetFloat(s_NormalScale, s.normalScale);
                m_Mat.SetFloat(s_Threshold,   s.threshold);
                m_Mat.SetFloat(s_Debug,       s.debug);
                m_Mat.SetFloat(s_FlatFaceJump, s.flatFaceJumpOnly ? 1f : 0f);
            }

            class PassData
            {
                public Material mat;
                public TextureHandle src;
                public TextureHandle lineNormals;
                public bool lineOn;
            }

            class LinePassData
            {
                public RendererListHandle list;
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

                // 얼굴 렌더러를 원본 노멀로 한 번 더 그린다. 렌더러의 재질 대신 원본 노멀 셰이더로 그리되 재질 값은 그대로 받는다(override shader).
                // 깊이는 따로 두고 xyz 노멀, w 카메라까지 거리를 남긴다. 소벨이 장면 깊이와 같은 자리에서만 이 노멀을 쓴다.
                TextureHandle lineTex = TextureHandle.nullHandle;
                if (m_LineShader != null)
                {
                    var rd = frameData.Get<UniversalRenderingData>();
                    var light = frameData.Get<UniversalLightData>();
                    var ds = CreateDrawingSettings(s_DepthOnly, rd, cam, light, SortingCriteria.CommonOpaque);
                    ds.overrideShader = m_LineShader;
                    ds.overrideShaderPassIndex = 0;
                    var list = renderGraph.CreateRendererList(new RendererListParams(rd.cullResults, ds, new FilteringSettings(RenderQueueRange.opaque, -1, OriginalNormals.LineLayer)));

                    var ndesc = renderGraph.GetTextureDesc(res.cameraColor);
                    ndesc.name = "_FBX2NPRLineNormals";
                    ndesc.format = GraphicsFormat.R32G32B32A32_SFloat;
                    ndesc.msaaSamples = MSAASamples.None;
                    ndesc.clearBuffer = true;
                    ndesc.clearColor = Color.clear;
                    lineTex = renderGraph.CreateTexture(ndesc);
                    var ddesc = renderGraph.GetTextureDesc(res.cameraColor);
                    ddesc.name = "_FBX2NPRLineDepth";
                    ddesc.depthBufferBits = DepthBits.Depth32;
                    ddesc.msaaSamples = MSAASamples.None;
                    ddesc.clearBuffer = true;
                    var lineDepth = renderGraph.CreateTexture(ddesc);

                    using (var lb = renderGraph.AddRasterRenderPass<LinePassData>("FBX2NPR Line Normals", out var ld))
                    {
                        ld.list = list;
                        lb.UseRendererList(list);
                        lb.SetRenderAttachment(lineTex, 0, AccessFlags.Write);
                        lb.SetRenderAttachmentDepth(lineDepth, AccessFlags.Write);
                        lb.SetRenderFunc((LinePassData d, RasterGraphContext ctx) => ctx.cmd.DrawRendererList(d.list));
                    }
                }

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("FBX2NPR Sobel Outline", out var pd))
                {
                    pd.mat = m_Mat;
                    pd.src = copy;
                    pd.lineNormals = lineTex;
                    pd.lineOn = lineTex.IsValid();
                    builder.UseTexture(copy, AccessFlags.Read);
                    if (res.cameraDepthTexture.IsValid())   builder.UseTexture(res.cameraDepthTexture);
                    if (res.cameraNormalsTexture.IsValid()) builder.UseTexture(res.cameraNormalsTexture);
                    if (pd.lineOn) builder.UseTexture(lineTex);
                    builder.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    {
                        d.mat.SetFloat(s_LineOn, d.lineOn ? 1f : 0f);
                        if (d.lineOn) d.mat.SetTexture(s_LineNormals, d.lineNormals);
                        Blitter.BlitTexture(ctx.cmd, d.src, new Vector4(1f, 1f, 0f, 0f), d.mat, 0);
                    });
                }
            }
        }
    }
}