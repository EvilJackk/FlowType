using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FlowType.Core;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace FlowType.Engine;

/// <summary>
/// One transcription attempt: the text, plus why it is empty when it is.
/// <see cref="TakeVerdict.NoSpeech"/> and <see cref="TakeVerdict.NoInput"/>
/// mean the audio was rejected before the model ran, which is a different
/// message to the user than "the model heard nothing".
/// </summary>
public readonly record struct Transcription(
    string Text, TakeVerdict Verdict, TakeMetrics Metrics, string? Language = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Local speech-to-text via Whisper.net (whisper.cpp). Prefers the Vulkan GPU
/// runtime when enabled and silently falls back to CPU if the GPU path can't
/// load or crashes (a known failure mode on some driver stacks). The loaded
/// model is cached; a processor is built per utterance so language, decoding
/// mode and the vocabulary-bias prompt can change between dictations.
/// </summary>
public sealed class Transcriber
{
    public static Transcriber Instance { get; } = new();

    /// <summary>Beam width for accurate decoding (OpenAI's reference default).</summary>
    public const int BeamSize = 5;

    /// <summary>
    /// How long a decode may take before it is treated as wedged. Four times
    /// real time plus a minute of slack covers the worst measured case by a
    /// wide margin (large-v3 beam search on CPU runs about 2x real time), so
    /// this only ever fires on a hang — never on a slow but working machine.
    /// Clamped to at least three minutes so a one-second utterance still gets
    /// room for a cold GPU kernel compile, and to half an hour so a stuck
    /// hands-free session cannot hold the app forever.
    /// </summary>
    public static TimeSpan DecodeBudget(double audioSeconds) =>
        TimeSpan.FromSeconds(Math.Clamp(Math.Ceiling(audioSeconds) * 4 + 60, 180, 1800));

    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _runtimeOrderSet;
    private bool _gpuBroken;
    private IDisposable? _logSubscription;
    private readonly List<string> _nativeLog = new();
    private readonly object _logGate = new();

    public bool IsReady => _factory != null;
    public bool IsLoading { get; private set; }
    public bool IsTranscribing { get; private set; }
    public string CurrentModelId { get; private set; } = "";

    /// <summary>Human description of the active compute path, for the UI.</summary>
    public string RuntimeLabel { get; private set; } = "";

    /// <summary>
    /// whisper.cpp's own log lines since the runtime loaded (device
    /// enumeration, which GPU it picked, fallbacks). Diagnostics only.
    /// </summary>
    public IReadOnlyList<string> NativeLog
    {
        get { lock (_logGate) return _nativeLog.ToArray(); }
    }

    public event Action? StateChanged;

    private Transcriber() { }

    /// <summary>
    /// whisper.cpp's default thread count is min(4, cores), which leaves most of
    /// a modern CPU idle on the fallback path. Physical cores (≈ logical / 2)
    /// are the sweet spot; more than that just contends for cache.
    /// </summary>
    private static int ThreadCount => Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// The native runtime search order can only be set before the first factory
    /// is created (the library loads once per process). With the Vulkan package
    /// present, ggml also ships its CPU backend, so machines without a Vulkan
    /// device still run — this ordering just decides which native library hosts
    /// the work.
    /// </summary>
    private void EnsureRuntimeOrder()
    {
        if (_runtimeOrderSet) return;
        _runtimeOrderSet = true;

        // Capture the native log before anything loads so the Vulkan device
        // list (printed once, at first load) ends up in --selftest output.
        _logSubscription ??= LogProvider.AddLogger((level, message) =>
        {
            var line = message?.Trim() ?? "";
            if (line.Length == 0) return;
            lock (_logGate)
            {
                if (_nativeLog.Count < 400) _nativeLog.Add($"[{level}] {line}");
            }
        });

        var wantGpu = SettingsStore.Instance.Settings.UseGpu && !_gpuBroken;
        RuntimeOptions.RuntimeLibraryOrder = wantGpu
            ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu }
            : new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
    }

    public async Task InitializeAsync()
    {
        var modelId = SettingsStore.Instance.Settings.SelectedModel;
        var model = ModelCatalog.ById(modelId);
        if (model == null || !model.IsDownloaded) return;
        await LoadModelAsync(modelId);
    }

    public async Task LoadModelAsync(string modelId)
    {
        var model = ModelCatalog.ById(modelId)
            ?? throw new ArgumentException($"Unknown model '{modelId}'.");
        if (!model.IsDownloaded)
        {
            throw new FileNotFoundException($"{model.DisplayName} is not downloaded yet.");
        }

        await _lock.WaitAsync();
        try
        {
            if (CurrentModelId == modelId && _factory != null) return;

            IsLoading = true;
            StateChanged?.Invoke();

            var newFactory = await Task.Run(() =>
            {
                EnsureRuntimeOrder();
                WhisperFactory created;
                try
                {
                    created = WhisperFactory.FromPath(model.FilePath, FactoryOptions());
                }
                catch when (!_gpuBroken && SettingsStore.Instance.Settings.UseGpu)
                {
                    // GPU runtime failed to come up — retry the load on CPU only.
                    // (Only effective if no native lib was loaded yet.)
                    _gpuBroken = true;
                    RuntimeOptions.RuntimeLibraryOrder =
                        new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
                    created = WhisperFactory.FromPath(model.FilePath, FactoryOptions());
                }
                WarmUp(created);
                return created;
            });

            _factory?.Dispose();
            _factory = newFactory;
            CurrentModelId = modelId;
            RuntimeLabel = DescribeRuntime();
        }
        finally
        {
            IsLoading = false;
            _lock.Release();
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Flash attention is a pure win on the GPU path (less memory traffic per
    /// decoder step, noticeably faster beam search) and is what whisper.cpp's
    /// own CLI recommends for GPU use. Kept off on CPU, where it is unproven
    /// across the CPU feature levels users run.
    /// </summary>
    private WhisperFactoryOptions FactoryOptions()
    {
        var gpu = SettingsStore.Instance.Settings.UseGpu && !_gpuBroken;
        return new WhisperFactoryOptions
        {
            UseGpu = gpu,
            UseFlashAttention = gpu,
        };
    }

    /// <summary>
    /// The first transcription on a fresh process pays for GPU pipeline
    /// creation and buffer allocation — measured at 1–2 s for the large models
    /// on an RTX 4070 (--bench "cold start"). Running a short embedded clip of
    /// clean speech through the freshly loaded model moves that cost to load
    /// time, when nobody is waiting for text. Best effort: any failure here is
    /// ignored and the first real dictation simply pays the cost instead.
    /// </summary>
    private static void WarmUp(WhisperFactory factory)
    {
        try
        {
            var samples = WarmUpSamples();
            if (samples.Length == 0) return;

            using var processor = factory.CreateBuilder()
                .WithThreads(ThreadCount)
                .WithLanguage("en")
                .WithGreedySamplingStrategy(g => g.WithBestOf(1))
                .Build();
            processor.Process(samples);
        }
        catch
        {
            // Warm-up is an optimisation, never a failure.
        }
    }

    /// <summary>
    /// Decode the embedded warm-up clip with language identification on and
    /// report what the model said the language was. This is the only way to
    /// know that Whisper.net actually populates <c>SegmentData.Language</c> on
    /// this version — and the filler gate now depends on it, so --selftest
    /// checks rather than assumes. Returns null if nothing was reported.
    /// </summary>
    internal async Task<string?> ProbeLanguageAsync()
    {
        var factory = _factory;
        if (factory == null) return null;
        var samples = WarmUpSamples();
        if (samples.Length == 0) return null;

        await using var processor = factory.CreateBuilder()
            .WithThreads(ThreadCount)
            .WithLanguageDetection()
            .WithGreedySamplingStrategy(g => g.WithBestOf(1))
            .Build();

        await foreach (var segment in processor.ProcessAsync(samples))
        {
            if (!string.IsNullOrWhiteSpace(segment.Language)) return segment.Language;
        }
        return null;
    }

    /// <summary>The embedded 2 s clip of clean speech, as float samples.</summary>
    private static float[] WarmUpSamples()
    {
        try
        {
            using var stream = typeof(Transcriber).Assembly
                .GetManifestResourceStream("FlowType.Resources.warmup.wav");
            if (stream == null) return Array.Empty<float>();
            using var reader = new WaveFileReader(stream);
            var provider = reader.ToSampleProvider();
            var samples = new List<float>();
            var buffer = new float[8192];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                samples.AddRange(buffer.Take(read));
            }
            return samples.ToArray();
        }
        catch
        {
            return Array.Empty<float>();
        }
    }

    public void UnloadIfCurrent(string modelId)
    {
        if (CurrentModelId != modelId) return;
        _factory?.Dispose();
        _factory = null;
        CurrentModelId = "";
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Transcribe a 16 kHz mono WAV. <paramref name="vocabulary"/> feeds
    /// whisper's initial prompt so custom names/terms are recognized — the
    /// local equivalent of Wispr Flow's dictionary-aware "name spelling".
    /// <paramref name="accurate"/> overrides the Settings decoding mode (used
    /// by the benchmark); null follows the user's setting.
    /// </summary>
    public async Task<Transcription> TranscribeAsync(
        string wavPath, string language, IReadOnlyList<string> vocabulary,
        CancellationToken ct = default, bool? accurate = null,
        IReadOnlyList<string>? spokenForms = null)
    {
        await _lock.WaitAsync(ct);
        var factory = _factory;
        if (factory == null)
        {
            _lock.Release();
            throw new InvalidOperationException("No AI model loaded yet.");
        }
        IsTranscribing = true;
        StateChanged?.Invoke();
        try
        {
            return await Task.Run(async () =>
            {
                var take = AudioConditioner.Prepare(ReadSamples(wavPath));
                var samples = take.Samples;
                if (samples.Length == 0)
                {
                    return new Transcription("", take.Verdict, take.Metrics, null);
                }

                var settings = SettingsStore.Instance.Settings;
                var useBeam = accurate ?? settings.AccurateDecoding;

                var builder = factory.CreateBuilder()
                    .WithThreads(ThreadCount);

                var model = ModelCatalog.ById(CurrentModelId);
                var effectiveLanguage = ResolveLanguage(
                    language, model?.EnglishOnly == true,
                    samples.Length / (double)AudioConditioner.SampleRate,
                    settings.LastDetectedLanguage);
                builder = effectiveLanguage == "auto"
                    ? builder.WithLanguageDetection()
                    : builder.WithLanguage(effectiveLanguage);

                // Initial prompt conditions the decoder: the English variant's
                // sample text biases spelling, the user's dictionary words make
                // uncommon names/terms far likelier to be recognized.
                var variantPrompt = EnglishVariant.RecognitionPrompt(settings.EnglishVariant);
                var prompt = BuildPrompt(variantPrompt, vocabulary, spokenForms);
                if (prompt != null) builder = builder.WithPrompt(prompt);

                // Decoding. Temperature fallback (the reference implementation's
                // 0 → 0.2 → … ladder, gated by compression/entropy and mean
                // log-prob) is what rescues a garbled pass instead of emitting
                // it; whisper.cpp has these defaults too, but stating them here
                // pins the behaviour across library upgrades.
                builder = builder
                    .WithTemperature(0f)
                    .WithTemperatureInc(0.2f)
                    .WithEntropyThreshold(2.4f)
                    .WithLogProbThreshold(-1.0f)
                    .WithNoSpeechThreshold(0.6f)
                    // Don't let one window's text condition the next. Dictation
                    // is one short utterance, and carrying context forward is
                    // what turns a single misfire into a repeating loop.
                    .WithNoContext();

                // Beam search keeps five hypotheses alive per step instead of
                // committing to the single likeliest token. Greedy decoding is
                // where mumbled or clipped words go wrong: one bad token early
                // drags the rest of the phrase with it. Roughly 1.5–2× the
                // decode time; trivial on a GPU, still fine on CPU for the
                // short utterances dictation produces.
                builder = useBeam
                    ? builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(BeamSize))
                    // best_of 5 samples five candidates per temperature step,
                    // which is most of beam search's cost on the path whose
                    // whole purpose is speed. The candidates belong to the
                    // accurate profile; fast means fast.
                    : builder.WithGreedySamplingStrategy(g => g.WithBestOf(1));

                await using var processor = builder.Build();
                var sb = new StringBuilder();
                string? detected = null;
                await foreach (var segment in processor.ProcessAsync(samples, ct))
                {
                    detected ??= segment.Language;
                    sb.Append(segment.Text);
                }
                // What the model actually decoded, not what was asked for. The
                // formatter needs this: its filler lists are language-specific.
                var decoded = string.IsNullOrWhiteSpace(detected)
                    ? (effectiveLanguage == "auto" ? null : effectiveLanguage)
                    : detected;
                // Unintelligible audio makes the model echo the prompt or loop
                // a phrase; both are caught here before anything is typed.
                // The echo guard has to know about every word that went into
                // the prompt, or it under-fires on the half it cannot see.
                var promptTerms = spokenForms is { Count: > 0 }
                    ? vocabulary.Concat(spokenForms).ToList()
                    : vocabulary;
                var text = HallucinationFilter.Apply(
                    CleanNonSpeechMarkers(sb.ToString()), variantPrompt, promptTerms);
                return new Transcription(text, take.Verdict, take.Metrics, decoded);
            }, ct);
        }
        finally
        {
            IsTranscribing = false;
            _lock.Release();
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Which backend is *actually* doing the work.
    ///
    /// ggml loads its Vulkan backend dynamically, so on a machine with a
    /// missing or broken Vulkan loader the backend quietly fails to register,
    /// the model still loads on CPU, and nothing throws — the GPU retry below
    /// never fires. <c>RuntimeOptions.LoadedLibrary</c> still says "Vulkan" in
    /// that case, which is how a user ends up being told they are on the GPU
    /// while waiting five times as long. whisper.cpp announces every device it
    /// registers in its own log, so that is the honest source.
    /// </summary>
    private string DescribeRuntime()
    {
        var requested = "CPU";
        try
        {
            requested = RuntimeOptions.LoadedLibrary switch
            {
                RuntimeLibrary.Vulkan => "GPU (Vulkan)",
                RuntimeLibrary.Cuda => "GPU (CUDA)",
                _ => "CPU",
            };
        }
        catch
        {
            requested = _gpuBroken || !SettingsStore.Instance.Settings.UseGpu ? "CPU" : "GPU/CPU";
        }

        if (!requested.StartsWith("GPU")) return requested;
        return GpuDeviceRegistered() ? requested : "CPU (no GPU device found)";
    }

    /// <summary>True when whisper.cpp's log shows it registered a GPU backend.</summary>
    private bool GpuDeviceRegistered()
    {
        foreach (var line in NativeLog)
        {
            if (line.Contains("backend_init_gpu", StringComparison.OrdinalIgnoreCase)
                || line.Contains("found GPU device", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ggml_vulkan: 0 =", StringComparison.OrdinalIgnoreCase)
                || line.Contains("using Vulkan", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Which language to ask the decoder for.
    ///
    /// whisper's language identification reads the first 30-second window, so
    /// on a two-second push-to-talk clip it is guessing from almost nothing —
    /// exactly the regime where it gets it wrong and the wrong filler list then
    /// deletes real words. Short "auto" clips therefore inherit the last
    /// language a long clip actually decoded, which for an English-only user is
    /// simply always English.
    /// </summary>
    internal static string ResolveLanguage(string setting, bool englishOnlyModel,
        double audioSeconds, string? lastDetected)
    {
        if (englishOnlyModel) return "en";
        if (setting != "auto") return setting;
        if (audioSeconds >= LanguageDetectionSeconds) return "auto";
        return string.IsNullOrWhiteSpace(lastDetected) ? "en" : lastDetected!;
    }

    /// <summary>whisper's LID window: below this, detection is a coin toss.</summary>
    internal const double LanguageDetectionSeconds = 10.0;

    /// <summary>
    /// whisper.cpp keeps only the last <c>n_text_ctx / 2</c> tokens of the
    /// initial prompt — roughly 220 tokens. Overrunning that silently drops the
    /// *front* of the prompt, which is where the spelling convention lives, so
    /// the prompt is capped well inside the limit.
    /// </summary>
    internal const int MaxPromptChars = 900;

    /// <summary>
    /// Compose the decoder's initial prompt. Naming the two roles explicitly
    /// conditions the model better than a bare comma list, because the prompt is
    /// continued as text: a labelled list reads as a glossary rather than as the
    /// start of a sentence the model should keep writing.
    ///
    /// Order is load-bearing. whisper.cpp keeps the *last* n_text_ctx/2 tokens
    /// of an over-long prompt, so the section that must survive goes last — the
    /// spellings, which are what the user actually wants written. Spoken forms
    /// are the hint about how the model mishears them; useful, but expendable.
    /// </summary>
    internal static string? BuildPrompt(string? variantPrompt,
        IReadOnlyList<string> spellings, IReadOnlyList<string>? spokenForms = null)
    {
        var budget = MaxPromptChars;
        var variant = string.IsNullOrWhiteSpace(variantPrompt) ? null : variantPrompt!.Trim();
        if (variant != null) budget -= variant.Length + 1;

        // Spellings are budgeted first even though they are rendered last.
        var spellingSection = Section("Preferred spellings", spellings, ref budget);
        var spokenSection = Section("Possible spoken forms", spokenForms, ref budget);

        var parts = new List<string>();
        if (variant != null) parts.Add(variant);
        if (spokenSection != null) parts.Add(spokenSection);
        if (spellingSection != null) parts.Add(spellingSection);

        if (parts.Count == 0) return null;
        var prompt = string.Join(" ", parts);
        // Should never trigger given the budgeting above; if it ever does, keep
        // the tail, because that is the half whisper.cpp would keep too.
        return prompt.Length <= MaxPromptChars ? prompt : prompt[^MaxPromptChars..];
    }

    private static string? Section(string label, IReadOnlyList<string>? terms, ref int budget)
    {
        if (terms is not { Count: > 0 }) return null;
        var overhead = label.Length + 3;      // "Label: " and the closing period
        if (budget <= overhead) return null;

        var remaining = budget - overhead;
        var kept = new List<string>();
        foreach (var term in terms)
        {
            var trimmed = SanitizeTerm(term);
            if (trimmed.Length == 0) continue;
            var cost = trimmed.Length + (kept.Count > 0 ? 2 : 0);
            if (cost > remaining) break;
            remaining -= cost;
            kept.Add(trimmed);
        }
        if (kept.Count == 0) return null;

        var section = $"{label}: {string.Join(", ", kept)}.";
        budget -= section.Length + 1;
        return section;
    }

    /// <summary>
    /// A dictionary entry can be pasted in and carry control characters or a
    /// non-breaking space; either goes straight into the decoder's prompt and
    /// wastes budget or confuses tokenisation.
    /// </summary>
    internal static string SanitizeTerm(string term)
    {
        var sb = new StringBuilder(term.Length);
        var lastWasSpace = false;
        foreach (var c in term)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && !lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
                continue;
            }
            sb.Append(c);
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }

    /// <summary>whisper.cpp's own non-speech annotations, and nothing else.</summary>
    private const string MarkerWords = "blank_audio|sound|music|noise|inaudible|applause|laughter|silence";

    private static readonly Regex MarkerPattern =
        new($@"\[(?:{MarkerWords})\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strip whisper.cpp's non-speech annotations ([BLANK_AUDIO], [MUSIC], ♪,
    /// a lone "(wind blowing)") so silence never types garbage.
    ///
    /// Deliberately narrow. Stripping *any* bracketed run — which is what this
    /// used to do — quietly destroys ordinary dictation: "See note [1] and [2]"
    /// became "See note and", "Compare a[0] to a[1]" became "Compare a to a",
    /// and "[sic]" vanished from quotations.
    /// </summary>
    internal static string CleanNonSpeechMarkers(string text)
    {
        var trimmed = text.Trim();

        // A take that is nothing but a marker is nothing at all.
        var whole = trimmed.ToLowerInvariant();
        if (whole.Length == 0) return "";
        if (MarkerPattern.IsMatch(whole) && MarkerPattern.Replace(whole, "").Trim().Length == 0)
        {
            return "";
        }
        if (Regex.IsMatch(trimmed, @"^\([^)]*\)$")) return "";

        var cleaned = MarkerPattern.Replace(trimmed, "");
        cleaned = cleaned.Replace("♪", "");
        // Spaces and tabs only: \s would swallow the blank line between two
        // paragraphs that "new paragraph" was asked to create.
        return Regex.Replace(cleaned, @"[ 	]{2,}", " ").Trim();
    }

    /// <summary>Read a 16 kHz mono WAV to floats (also used by --conditioncheck).</summary>
    internal static float[] ReadSamples(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        var provider = reader.ToSampleProvider();
        var samples = new List<float>(capacity: (int)(reader.SampleCount + 16));
        var buffer = new float[16384];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);
        }
        return samples.ToArray();
    }
}
