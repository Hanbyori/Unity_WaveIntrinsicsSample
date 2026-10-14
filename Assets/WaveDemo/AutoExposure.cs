using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum ReduceMode { Atomic, GroupShared, Wave }

[RequireComponent(typeof(Camera))]
public class AutoExposure : MonoBehaviour
{
    public static AutoExposure Active { get; private set; }
    public static readonly string[] Labels = { "A  Atomic", "B  groupshared", "C  Wave" };

    public ComputeShader shader;
    public Volume volume;
    public ReduceMode mode = ReduceMode.Wave;
    public bool autoExposure = false;
    public float targetLuminance = 0.3f;
    public float adaptSpeed = 3f;
    public Vector2 evRange = new Vector2(-8f, 8f);
    public float atomicMaxAverage = 2f;
    public float AtomicScale { get; private set; }
    public int timingWindow = 120;
    public bool showOverlay = true;

    public Camera Camera { get; private set; }
    public GraphicsBuffer Totals { get; private set; }
    public GraphicsBuffer[] Partials { get; } = new GraphicsBuffer[3];
    public ProfilingSampler[] Samplers { get; private set; }
    public int GroupCount { get; private set; }
    public int PixelCount { get; private set; }
    public int Frame { get; private set; }

    public readonly float[] Average = new float[3];
    public readonly float[] Max = new float[3];
    public readonly float[] GpuMs = new float[3];
    public float Ev { get; private set; }

    ColorAdjustments colorAdjustments;
    readonly bool[] pending = new bool[3];
    float[,] timingHistory;
    readonly int[] timingCount = new int[3];
    GUIStyle style;

    void OnEnable()
    {
        Application.runInBackground = true;
        Camera = GetComponent<Camera>();
        Totals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, sizeof(uint));
        Samplers = new ProfilingSampler[3];
        for (int i = 0; i < 3; i++)
            Samplers[i] = new ProfilingSampler("Reduce " + LuminanceReduce.Kernels[i]) { enableRecording = true };
        if (volume != null)
            volume.profile.TryGet(out colorAdjustments);
        Active = this;
    }

    void OnDisable()
    {
        if (Active == this)
            Active = null;
        Totals?.Release();
        for (int i = 0; i < Partials.Length; i++)
        {
            Partials[i]?.Release();
            Partials[i] = null;
        }
    }

    public void EnsureSize(int pixelCount)
    {
        PixelCount = pixelCount;
        AtomicScale = Mathf.Pow(2f, Mathf.Floor(Mathf.Log(uint.MaxValue / (pixelCount * atomicMaxAverage), 2f)));
        GroupCount = (pixelCount + LuminanceReduce.GroupSize - 1) / LuminanceReduce.GroupSize;
        for (int i = 1; i < 3; i++)
        {
            if (Partials[i] != null && Partials[i].count >= GroupCount)
                continue;
            Partials[i]?.Release();
            Partials[i] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GroupCount, sizeof(float) * 2);
        }
    }

    void Update()
    {
        if (Active == null || !Active.isActiveAndEnabled)
            Active = this;
        if (Active != this)
            return;

        Frame++;
        if (timingHistory == null || timingHistory.GetLength(1) != timingWindow)
            timingHistory = new float[3, timingWindow];
        for (int i = 0; i < 3; i++)
        {
            RequestReadback(i);
            float ms = Samplers[i].gpuElapsedTime;
            if (ms > 0f)
            {
                timingHistory[i, timingCount[i] % timingWindow] = ms;
                timingCount[i]++;
                int n = Mathf.Min(timingCount[i], timingWindow);
                float sum = 0f;
                for (int k = 0; k < n; k++)
                    sum += timingHistory[i, k];
                GpuMs[i] = sum / n;
            }
        }

        float avg = Average[(int)mode];
        float goal = autoExposure
            ? Mathf.Clamp(Mathf.Log(targetLuminance / Mathf.Max(avg, 1e-4f), 2f), evRange.x, evRange.y)
            : 0f;
        Ev = Mathf.Lerp(Ev, goal, 1f - Mathf.Exp(-adaptSpeed * Time.deltaTime));

        if (colorAdjustments != null)
        {
            colorAdjustments.postExposure.overrideState = true;
            colorAdjustments.postExposure.value = Ev;
        }
    }

    void RequestReadback(int i)
    {
        if (pending[i] || (i > 0 && Partials[i] == null) || PixelCount == 0)
            return;
        pending[i] = true;

        int pixels = PixelCount;
        if (i == 0)
        {
            float scale = AtomicScale;
            AsyncGPUReadback.Request(Totals, r =>
            {
                pending[0] = false;
                if (r.hasError)
                    return;
                NativeArray<uint> t = r.GetData<uint>();
                Average[0] = (float)(t[0] / (double)scale / pixels);
                Max[0] = System.BitConverter.Int32BitsToSingle((int)t[1]);
            });
            return;
        }

        int groups = GroupCount;
        AsyncGPUReadback.Request(Partials[i], groups * sizeof(float) * 2, 0, r =>
        {
            pending[i] = false;
            if (r.hasError)
                return;
            NativeArray<Vector2> p = r.GetData<Vector2>();
            double sum = 0;
            float max = 0;
            for (int g = 0; g < groups; g++)
            {
                sum += p[g].x;
                max = Mathf.Max(max, p[g].y);
            }
            Average[i] = (float)(sum / pixels);
            Max[i] = max;
        });
    }

    void Label(Rect r, string text)
    {
        GUI.Label(r, "<color=#FFFFFF>" + text + "</color>", style);
    }

    void OnGUI()
    {
        if (!showOverlay || Active != this)
            return;
        float s = Screen.height / 1080f;
        style ??= new GUIStyle(GUI.skin.label) { richText = true };
        style.fontSize = Mathf.RoundToInt(28 * s);

        var rect = new Rect(24 * s, 24 * s, 820 * s, (colorAdjustments == null ? 268 : 330) * s);
        GUI.color = new Color(0, 0, 0, 0.65f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;

        float x = rect.x + 20 * s;
        float y = rect.y + 14 * s;
        float line = 40 * s;
        float[] cols = { 0, 270, 450, 600 };

        Label(new Rect(x, y, rect.width, line),
            $"<b>Luminance reduction</b>  {Screen.width}x{Screen.height} HDR, {GroupCount:N0} groups");
        y += line * 1.3f;

        string[] header = { "<color=#AAB2BD>Kernel</color>", "<color=#AAB2BD>GPU time</color>", "<color=#AAB2BD>Avg</color>", "<color=#AAB2BD>Max</color>" };
        for (int c = 0; c < 4; c++)
            Label(new Rect(x + cols[c] * s, y, 300 * s, line), header[c]);
        y += line;

        float fastest = Mathf.Min(GpuMs[0], Mathf.Min(GpuMs[1], GpuMs[2]));
        for (int i = 0; i < 3; i++)
        {
            bool best = GpuMs[i] > 0f && GpuMs[i] == fastest;
            string time = GpuMs[i] > 0f ? $"{GpuMs[i] * 1000f:F0} µs" : "n/a";
            if (best)
                time = $"<color=#7CFC9A>{time}</color>";
            string[] cells = { Labels[i], time, $"{Average[i]:F4}", $"{Max[i]:F3}" };
            for (int c = 0; c < 4; c++)
                Label(new Rect(x + cols[c] * s, y, 300 * s, line), cells[c]);
            y += line;
        }

        if (colorAdjustments == null)
            return;
        y += line * 0.3f;
        string state = autoExposure ? $"{Ev:+0.00;-0.00} EV" : "off";
        Label(new Rect(x, y, rect.width, line),
            $"<color=#AAB2BD>Auto exposure from {Labels[(int)mode].Substring(3)}:</color> {state}");
    }
}
