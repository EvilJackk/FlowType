namespace FlowType.Engine;

/// <summary>What a take actually contained, decided before whisper ever sees it.</summary>
public enum TakeVerdict
{
    /// <summary>Sustained voiced energy — definitely worth transcribing.</summary>
    Speech,

    /// <summary>Neither obviously speech nor obviously silence. Transcribe anyway.</summary>
    Uncertain,

    /// <summary>Confidently silence or room tone. Transcribing would invent words.</summary>
    NoSpeech,

    /// <summary>The capture is digitally dead — a muted or disconnected microphone.</summary>
    NoInput,
}

/// <summary>Everything measured about a take, for diagnostics (--selftest, --bench).</summary>
public readonly record struct TakeMetrics(
    double Rms,
    float Peak,
    float MaxWindowRms,
    int MsAboveVoiceFloor,
    bool SustainedVoice,
    bool SpeechLikeModulation,
    float AppliedGain,
    int InputMs,
    int TrimmedMs);

public readonly record struct ConditionedTake(float[] Samples, TakeVerdict Verdict, TakeMetrics Metrics)
{
    /// <summary>False when the take was rejected before the model.</summary>
    public bool ShouldTranscribe => Samples.Length > 0;
}

/// <summary>
/// Cleans a captured take before it reaches whisper, and decides whether it is
/// worth sending at all.
///
///   1. High-pass at 80 Hz — removes DC offset, desk thumps and fan rumble
///      that otherwise smear the low mel bands.
///   2. Measure — 20 ms window RMS, aggregate RMS, peak. Every decision below
///      comes from these rather than from a single peak sample.
///   3. Verdict — silence and room tone are rejected outright instead of being
///      handed to a model that will confidently invent "Thank you." for them.
///   4. Gain — peak normalisation, but the large boost is unlocked only for
///      audio whose envelope actually swings like speech, so steady hiss is
///      never amplified into a wall of noise that then gets decoded.
///   5. Trim — long digital silence on the end (hands-free sessions, a slow
///      release) is cut back to 300 ms of context.
///   6. Pad — whisper.cpp refuses anything under 1000 ms outright, and both
///      ends want a little silence so the first and last words are not clipped
///      mid-phoneme.
///
/// The thresholds are voicetypr's, which calibrated them against real capture
/// telemetry rather than guessing (audio/silence_detector.rs,
/// audio/speech_evidence.rs, audio/normalizer.rs in that project). Where this
/// version deviates it is noted at the constant.
///
/// Pure functions over float samples (16 kHz mono, -1..1).
/// </summary>
public static class AudioConditioner
{
    public const int SampleRate = 16000;

    /// <summary>20 ms at 16 kHz — the analysis window everything below uses.</summary>
    public const int WindowSamples = SampleRate / 50;

    private const int WindowMs = 1000 / 50;

    /// <summary>Window RMS at or above this counts as voiced energy (about −46 dBFS).</summary>
    public const float VoiceRmsFloor = 0.005f;

    /// <summary>Voiced windows that make a take definitely speech — 300 ms.</summary>
    public const int SustainedVoiceWindows = 15;

    /// <summary>Aggregate RMS below this, with no voiced run, is evidence of no speech.</summary>
    public const double NoSpeechRmsFloor = 0.002;

    /// <summary>…provided nothing peaks above this. A spoken word peaks far higher.</summary>
    public const float NoSpeechPeakCeiling = 0.05f;

    /// <summary>
    /// …and the take holds no more above-floor audio than this: one pop, not a
    /// word. Measured in 20 ms steps here (voicetypr uses 5 ms), so in practice
    /// this means "at most one window", which is the conservative direction.
    /// </summary>
    public const int NoSpeechMaxTransientMs = 20;

    private const float TargetPeak = 0.9f;

    /// <summary>
    /// Gain ceiling for anything not modulated like speech (+24 dB). This is
    /// FlowType's long-standing cap, kept deliberately: the point of the
    /// modulation gate is to hand *more* headroom to real speech, never less to
    /// anything else. voicetypr caps the un-gated path at 10x; benching that
    /// against the hard corpus made mumbled clips slightly worse (small.en
    /// accurate 9.5 % -> 11.8 % word errors), because only 4 of 20 reverberant
    /// mumble clips read as modulated — reverb fills in the quiet gaps the
    /// detector looks for.
    /// </summary>
    private const float StandardMaxGain = 16f;

    /// <summary>Gain ceiling once the envelope proves it is speech (+30 dB).</summary>
    private const float SpeechMaxGain = 32f;

    /// <summary>Loud-to-quiet window energy ratio of real speech (~12 dB).</summary>
    private const float ModulationRatio = 4f;

    private const float SilenceRmsFloor = 1e-4f;

    // Trailing-silence trim.
    private const float TrailingSilenceRms = VoiceRmsFloor * 0.25f;   // 0.00125
    private const int MinTrailingSilenceWindows = 35;                 // 700 ms before trimming
    private const int RetainTrailingWindows = 15;                     // keep 300 ms of context
    private const int MinRetainedSamples = SampleRate / 2;            // never cut below 0.5 s

    // Padding.
    private const double LeadInSeconds = 0.15;
    private const double TailSeconds = 0.45;
    private const double MinimumSeconds = 1.25;                       // whisper.cpp's floor is 1.0 s

    /// <summary>
    /// Condition a take and decide its fate. When the verdict is
    /// <see cref="TakeVerdict.NoSpeech"/> or <see cref="TakeVerdict.NoInput"/>
    /// the sample array comes back empty and nothing should be transcribed.
    /// </summary>
    public static ConditionedTake Prepare(float[] samples)
    {
        if (samples.Length == 0)
        {
            return new ConditionedTake(samples, TakeVerdict.NoInput, default);
        }

        var work = (float[])samples.Clone();
        HighPass(work, cutoffHz: 80);

        var windows = WindowRms(work);
        var required = RequiredVoicedWindows(windows.Length);
        var peak = Peak(work);
        var rms = Rms(work);
        var maxWindow = windows.Length == 0 ? 0f : windows.Max();
        var msAboveFloor = windows.Count(w => w >= VoiceRmsFloor) * WindowMs;
        var sustained = HasSustainedVoice(windows, required);
        var modulated = HasSpeechLikeModulation(windows, required);
        var inputMs = (int)(work.Length * 1000L / SampleRate);

        var metrics = new TakeMetrics(rms, peak, maxWindow, msAboveFloor, sustained, modulated,
            AppliedGain: 1f, InputMs: inputMs, TrimmedMs: 0);

        var verdict = Classify(rms, peak, msAboveFloor, sustained, modulated);
        if (verdict is TakeVerdict.NoInput or TakeVerdict.NoSpeech)
        {
            return new ConditionedTake(Array.Empty<float>(), verdict, metrics);
        }

        // The 32× ceiling exists for genuinely quiet dictation; steady noise
        // keeps the old 16× one, so hiss is never lifted to full scale and
        // decoded into words.
        var gain = peak > 0
            ? Math.Min(modulated ? SpeechMaxGain : StandardMaxGain, TargetPeak / peak)
            : 1f;
        if (gain > 1.02f)
        {
            for (var i = 0; i < work.Length; i++)
            {
                work[i] = Math.Clamp(work[i] * gain, -1f, 1f);
            }
        }
        else
        {
            gain = 1f;
        }

        var trimmed = TrimTrailingSilence(work, required);
        var trimmedMs = (int)((work.Length - trimmed.Length) * 1000L / SampleRate);

        metrics = metrics with { AppliedGain = gain, TrimmedMs = trimmedMs };
        return new ConditionedTake(Pad(trimmed), verdict, metrics);
    }

    internal static TakeVerdict Classify(double rms, float peak, int msAboveFloor,
        bool sustainedVoice, bool modulated)
    {
        if (sustainedVoice || modulated) return TakeVerdict.Speech;
        if (rms == 0 && peak == 0) return TakeVerdict.NoInput;

        // All three signals must agree. An aggregate floor on its own rejects a
        // quiet word buried in long silence; a window maximum on its own lets a
        // single pop through (voicetypr saw a 746 ms "silent" capture with one
        // transient hallucinate a transcript). Together they only fire on
        // genuine silence or room tone.
        if (rms > 0 && rms < NoSpeechRmsFloor && peak < NoSpeechPeakCeiling
            && msAboveFloor <= NoSpeechMaxTransientMs)
        {
            return TakeVerdict.NoSpeech;
        }
        return TakeVerdict.Uncertain;
    }

    /// <summary>
    /// 300 ms of voice is the bar for a normal take, but push-to-talk produces
    /// one-word takes ("yes", "okay") that are barely longer than that. For a
    /// short take, ask instead that half of it be voiced — proportionally
    /// stricter, and it keeps single words out of the un-boosted path.
    /// </summary>
    private static int RequiredVoicedWindows(int windowCount) =>
        Math.Min(SustainedVoiceWindows, Math.Max(3, windowCount / 2));

    // ----- measurement -----

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

    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    internal static float[] WindowRms(float[] samples)
    {
        var count = samples.Length / WindowSamples;
        if (count == 0) return Array.Empty<float>();
        var result = new float[count];
        for (var w = 0; w < count; w++)
        {
            double sum = 0;
            var start = w * WindowSamples;
            for (var i = 0; i < WindowSamples; i++)
            {
                double v = samples[start + i];
                sum += v * v;
            }
            result[w] = (float)Math.Sqrt(sum / WindowSamples);
        }
        return result;
    }

    private static bool HasSustainedVoice(float[] windows, int required)
    {
        var run = 0;
        foreach (var w in windows)
        {
            if (w >= VoiceRmsFloor)
            {
                if (++run >= required) return true;
            }
            else run = 0;
        }
        return false;
    }

    /// <summary>
    /// Speech swings between loud vowels and near-silent gaps; steady noise and
    /// pure tones have a flat envelope. Comparing the take's own 90th- against
    /// its 10th-percentile window energy keeps the big boost available on a
    /// real (slightly noisy) microphone while refusing it to steady signals at
    /// any absolute level.
    /// </summary>
    internal static bool HasSpeechLikeModulation(float[] windows, int required)
    {
        if (windows.Length < required) return false;
        if (windows.Count(w => w >= VoiceRmsFloor) < required) return false;

        var sorted = (float[])windows.Clone();
        Array.Sort(sorted);
        var floor = Math.Max(sorted[sorted.Length / 10], SilenceRmsFloor);
        var loud = sorted[sorted.Length * 9 / 10];
        return loud >= floor * ModulationRatio;
    }

    // ----- shaping -----

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

    /// <summary>
    /// A hands-free session left running, or a long pause before the tap that
    /// ends it, leaves seconds of silence on the end — and whisper fills
    /// silence with invention. Anything past 700 ms of quiet is cut back to
    /// 300 ms of context, but only when the take clearly contained speech, so a
    /// quiet recording is never shortened into nothing.
    /// </summary>
    private static float[] TrimTrailingSilence(float[] samples, int required)
    {
        if (samples.Length <= MinRetainedSamples) return samples;

        var windows = WindowRms(samples);
        if (!HasSustainedVoice(windows, required)) return samples;

        var silentTail = 0;
        for (var i = windows.Length - 1; i >= 0 && windows[i] <= TrailingSilenceRms; i--) silentTail++;
        if (silentTail < MinTrailingSilenceWindows) return samples;

        var cutWindow = Math.Min(windows.Length - silentTail + RetainTrailingWindows, windows.Length);
        var cutSamples = Math.Min(cutWindow * WindowSamples, samples.Length);
        if (cutSamples < MinRetainedSamples) return samples;

        return samples[..cutSamples];
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
