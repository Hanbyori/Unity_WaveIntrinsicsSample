using System;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class LuminanceReduce : IDisposable
{
    public const int GroupSize = 256;
    public static readonly string[] Kernels = { "ReduceAtomic", "ReduceGroupShared", "ReduceWave" };
    const double AtomicScale = 256.0;
    static readonly Vector3 Luma = new Vector3(0.2126f, 0.7152f, 0.0722f);

    public readonly ComputeShader Shader;
    public readonly Texture2D Texture;
    public readonly int PixelCount;
    public readonly int GroupCount;
    public readonly double CpuAverage;
    public readonly float CpuMax;

    readonly ComputeBuffer totals;
    readonly ComputeBuffer partials;

    public LuminanceReduce(ComputeShader shader, int width, int height, int seed = 1234)
    {
        Shader = shader;
        PixelCount = width * height;
        GroupCount = (PixelCount + GroupSize - 1) / GroupSize;

        Texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { name = "LuminanceSource" };
        var pixels = new Color32[PixelCount];
        var rng = new System.Random(seed);
        double sum = 0;
        float max = 0;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            float g = 0.6f * x / width;
            byte r = (byte)(255 * (g + 0.2f * (float)rng.NextDouble()));
            byte gr = (byte)(255 * (0.5f * g + 0.2f * (float)rng.NextDouble()));
            byte b = (byte)(255 * (0.3f + 0.2f * (float)rng.NextDouble()));
            pixels[y * width + x] = new Color32(r, gr, b, 255);
        }
        pixels[rng.Next(PixelCount)] = new Color32(235, 240, 230, 255);

        foreach (var p in pixels)
        {
            float l = Luma.x * (p.r / 255f) + Luma.y * (p.g / 255f) + Luma.z * (p.b / 255f);
            sum += l;
            max = Mathf.Max(max, l);
        }
        CpuAverage = sum / PixelCount;
        CpuMax = max;

        Texture.SetPixels32(pixels);
        Texture.Apply(false, false);

        totals = new ComputeBuffer(2, sizeof(uint));
        partials = new ComputeBuffer(GroupCount, sizeof(float) * 2);

        foreach (var k in Kernels)
            Bind(shader.FindKernel(k));
        Bind(shader.FindKernel("ClearTotals"));
        shader.SetInt("_Width", width);
        shader.SetInt("_PixelCount", PixelCount);
        shader.SetFloat("_AtomicScale", (float)AtomicScale);
    }

    void Bind(int kernel)
    {
        Shader.SetTexture(kernel, "_Source", Texture);
        Shader.SetBuffer(kernel, "_Totals", totals);
        Shader.SetBuffer(kernel, "_Partials", partials);
    }

    public void RecordDispatch(CommandBuffer cmd, string kernel)
    {
        if (kernel == "ReduceAtomic")
            cmd.DispatchCompute(Shader, Shader.FindKernel("ClearTotals"), 1, 1, 1);
        cmd.DispatchCompute(Shader, Shader.FindKernel(kernel), GroupCount, 1, 1);
    }

    public (double average, float max) Run(string kernel)
    {
        using (var cmd = new CommandBuffer { name = kernel })
        {
            RecordDispatch(cmd, kernel);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        if (kernel == "ReduceAtomic")
        {
            var t = new uint[2];
            totals.GetData(t);
            return (t[0] / AtomicScale / PixelCount, BitConverter.ToSingle(BitConverter.GetBytes(t[1]), 0));
        }

        var p = new Vector2[GroupCount];
        partials.GetData(p);
        double sum = 0;
        float max = 0;
        foreach (var v in p)
        {
            sum += v.x;
            max = Mathf.Max(max, v.y);
        }
        return (sum / PixelCount, max);
    }

    public double TimeMicroseconds(string kernel, int repeat = 200, int trials = 7)
    {
        int k = Shader.FindKernel(kernel);
        var samples = new double[trials];
        using (var cmd = new CommandBuffer { name = kernel + " x" + repeat })
        {
            for (int i = 0; i < repeat; i++)
                cmd.DispatchCompute(Shader, k, GroupCount, 1, 1);

            var probe = new uint[1];
            for (int t = -2; t < trials; t++)
            {
                totals.GetData(probe);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Graphics.ExecuteCommandBuffer(cmd);
                totals.GetData(probe);
                sw.Stop();
                if (t >= 0)
                    samples[t] = sw.Elapsed.TotalMilliseconds * 1000.0 / repeat;
            }
        }
        Array.Sort(samples);
        return samples[trials / 2];
    }

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Texture.width}x{Texture.height}, {GroupCount} groups, {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}");
        sb.AppendLine($"CPU      avg={CpuAverage:F9} max={CpuMax:F9}");
        foreach (var k in Kernels)
        {
            var (avg, max) = Run(k);
            sb.AppendLine($"{k,-17} avg={avg:F9} (diff {avg - CpuAverage:E2}) max={max:F9} (diff {max - CpuMax:E2})");
        }
        foreach (var k in Kernels)
            sb.AppendLine($"{k,-17} {TimeMicroseconds(k):F1} us / dispatch");
        return sb.ToString();
    }

    public static string Verify(int width = 3840, int height = 2160)
    {
#if UNITY_EDITOR
        var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/WaveDemo/LuminanceReduce.compute");
        using (var r = new LuminanceReduce(shader, width, height))
            return r.Report();
#else
        return "editor only";
#endif
    }

    public void Dispose()
    {
        totals.Release();
        partials.Release();
        UnityEngine.Object.DestroyImmediate(Texture);
    }
}
