using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

public class LuminanceReduceFeature : ScriptableRendererFeature
{
    LuminanceReducePass pass;

    public override void Create()
    {
        pass = new LuminanceReducePass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var ae = AutoExposure.Active;
        if (ae != null && ae.shader != null && renderingData.cameraData.camera == ae.Camera)
            renderer.EnqueuePass(pass);
    }

    class LuminanceReducePass : ScriptableRenderPass
    {
        class PassData
        {
            public ComputeShader shader;
            public int[] kernels = new int[3];
            public int clearKernel;
            public ProfilingSampler[] samplers;
            public int firstKernel;
            public TextureHandle source;
            public BufferHandle totals;
            public BufferHandle[] partials = new BufferHandle[3];
            public int width;
            public int pixelCount;
            public int groupCount;
            public float atomicScale;
        }

        public LuminanceReducePass()
        {
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var ae = AutoExposure.Active;
            var resources = frameData.Get<UniversalResourceData>();
            var desc = frameData.Get<UniversalCameraData>().cameraTargetDescriptor;
            ae.EnsureSize(desc.width * desc.height);

            using (var builder = renderGraph.AddComputePass<PassData>("Luminance Reduce A/B/C", out var data))
            {
                data.shader = ae.shader;
                for (int i = 0; i < 3; i++)
                    data.kernels[i] = ae.shader.FindKernel(LuminanceReduce.Kernels[i]);
                data.clearKernel = ae.shader.FindKernel("ClearTotals");
                data.samplers = ae.Samplers;
                data.firstKernel = ae.Frame % 3;
                data.source = resources.activeColorTexture;
                data.totals = renderGraph.ImportBuffer(ae.Totals);
                data.partials[1] = renderGraph.ImportBuffer(ae.Partials[1]);
                data.partials[2] = renderGraph.ImportBuffer(ae.Partials[2]);
                data.width = desc.width;
                data.pixelCount = ae.PixelCount;
                data.groupCount = ae.GroupCount;
                data.atomicScale = ae.AtomicScale;

                builder.UseTexture(data.source, AccessFlags.Read);
                builder.UseBuffer(data.totals, AccessFlags.ReadWrite);
                builder.UseBuffer(data.partials[1], AccessFlags.Write);
                builder.UseBuffer(data.partials[2], AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (PassData d, ComputeGraphContext ctx) =>
                {
                    var cmd = ctx.cmd;
                    cmd.SetComputeIntParam(d.shader, "_Width", d.width);
                    cmd.SetComputeIntParam(d.shader, "_PixelCount", d.pixelCount);
                    cmd.SetComputeFloatParam(d.shader, "_AtomicScale", d.atomicScale);
                    cmd.SetComputeBufferParam(d.shader, d.clearKernel, "_Totals", d.totals);
                    cmd.DispatchCompute(d.shader, d.clearKernel, 1, 1, 1);

                    for (int n = 0; n < 3; n++)
                    {
                        int i = (d.firstKernel + n) % 3;
                        int k = d.kernels[i];
                        cmd.SetComputeTextureParam(d.shader, k, "_Source", d.source);
                        cmd.SetComputeBufferParam(d.shader, k, "_Totals", d.totals);
                        cmd.SetComputeBufferParam(d.shader, k, "_Partials", i == 0 ? d.partials[2] : d.partials[i]);
                        using (new ProfilingScope(cmd, d.samplers[i]))
                            cmd.DispatchCompute(d.shader, k, d.groupCount, 1, 1);
                    }
                });
            }
        }
    }
}
