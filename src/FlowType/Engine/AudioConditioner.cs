namespace FlowType.Engine;

/// <summary>
/// Cleans a captured take before it reaches whisper. Three cheap steps that
/// each fix a real failure seen with quiet or rushed dictation:
///
///   1. High-pass at 80 Hz — removes DC offset, desk thumps and fan rumble
///      that otherwise smear the low mel bands.
///   2. Peak normalisation — a mumbled or distant take often peaks at -30 dBFS;
///      lifting it toward full scale (capped so pure noise is never amplified
///      into a wall) gives the model the same loudness it was trained on.
///   3. Padding — whisper.cpp refuses anything under 1000 ms outright, and
///      both ends benefit from a little silence so the first and last words are
///      not cut mid-phoneme.
///
/// Pure functions over float samples (16 kHz mono, -1..1).
/// </summary>
public static class AudioConditioner
{
    public const int SampleRate = 16000;

    /// <summary>Peak below this is treated as no audio at all (about -54 dBFS).</summary>
    public const float SilencePeak = 0.002f;

    private const float TargetPeak = 0.9f;
    private const float MaxGain = 16f;           // +24 dB ceiling
    private const double LeadInSeconds = 0.15;
    private const double TailSeconds = 0.45;
    private const double MinimumSeconds = 1.25;  // whisper.cpp's floor is 1.0 s

    /// <summary>
    /// Returns the conditioned signal, or an empty array when the take is
    /// effectively silent (the caller then reports "didn't catch anything"
    /// instead of letting the model hallucinate a "Thank you.").
    /// </summary>
    public static float[] Prepare(float[] samples)
    {
        if (samples.Length == 0) return samples;

        var work = (float[])samples.Clone();
        HighPass(work, cutoffHz: 80);

        var peak = Peak(work);
        if (peak < SilencePeak) return Array.Empty<float>();

        var gain = Math.Min(MaxGain, TargetPeak / peak);
        if (gain > 1.02f)
        {
            for (var i = 0; i < work.Length; i++)
            {
                work[i] = Math.Clamp(work[i] * gain, -1f, 1f);
            }
        }

        return Pad(work);
    }

    public static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (var s in samples)
        {
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>Second-order Butterworth high-pass (RBJ cookbook), in place.</summary>
    private static void HighPass(float[] x, double cutoffHz)
    {
        const double q = 0.7071;
        var w0 = 2 * Math.PI * cutoffHz / SampleRate;
        var alpha = Math.Sin(w0) / (2 * q);
        var cos = Math.Cos(w0);
        var a0 = 1 + alpha;
        var b0 = (1 + cos) / 2 / a0;
        var b1 = -(1 + cos) / a0;
        var b2 = (1 + cos) / 2 / a0;
        var a1 = -2 * cos / a0;
        var a2 = (1 - alpha) / a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (var i = 0; i < x.Length; i++)
        {
            double xi = x[i];
            var y = b0 * xi + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = xi;
            y2 = y1; y1 = y;
            x[i] = (float)y;
        }
    }

    private static float[] Pad(float[] x)
    {
        var lead = (int)(LeadInSeconds * SampleRate);
        var tail = (int)(TailSeconds * SampleRate);
        var minimum = (int)(MinimumSeconds * SampleRate);
        var total = Math.Max(minimum, lead + x.Length + tail);
        var padded = new float[total];
        Array.Copy(x, 0, padded, lead, x.Length);
        return padded;
    }
}
