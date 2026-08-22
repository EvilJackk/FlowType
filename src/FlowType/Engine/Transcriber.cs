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
            using var stream = typeof(Transcriber).Assembly
                .GetManifestResourceStream("FlowType.Resources.warmup.wav");
            if (stream == null) return;
            using var reader = new WaveFileReader(stream);
            var provider = reader.ToSampleProvider();
            var samples = new List<float>();
            var buffer = new float[8192];
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                samples.AddRange(buffer.Take(read));
            }
            if (samples.Count == 0) return;

            using var processor = factory.CreateBuilder()
                .WithThreads(ThreadCount)
                .WithLanguage("en")
                .WithGreedySamplingStrategy(g => g.WithBestOf(1))
                .Build();
            processor.Process(samples.ToArray());
        }
        catch
        {
            // Warm-up is an optimisation, never a failure.
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
    public async Task<string> TranscribeAsync(
        string wavPath, string language, IReadOnlyList<string> vocabulary,
        CancellationToken ct = default, bool? accurate = null)
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
                var samples = AudioConditioner.Prepare(ReadSamples(wavPath));
                if (samples.Length == 0) return "";

                var settings = SettingsStore.Instance.Settings;
                var useBeam = accurate ?? settings.AccurateDecoding;

                var builder = factory.CreateBuilder()
                    .WithThreads(ThreadCount);

                var model = ModelCatalog.ById(CurrentModelId);
                var effectiveLanguage = model?.EnglishOnly == true ? "en" : language;
                builder = effectiveLanguage == "auto"
                    ? builder.WithLanguageDetection()
                    : builder.WithLanguage(effectiveLanguage);

                // Initial prompt conditions the decoder: the English variant's
                // sample text biases spelling, the user's dictionary words make
                // uncommon names/terms far likelier to be recognized.
                var promptParts = new List<string>();
                var variantPrompt = EnglishVariant.RecognitionPrompt(settings.EnglishVariant);
                if (variantPrompt != null) promptParts.Add(variantPrompt);
                if (vocabulary.Count > 0) promptParts.Add(string.Join(", ", vocabulary.Take(24)));
                var prompt = promptParts.Count > 0 ? string.Join(" ", promptParts) : null;
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
                    .WithNoSpeechThreshold(0.6f);

                // Beam search keeps five hypotheses alive per step instead of
                // committing to the single likeliest token. Greedy decoding is
                // where mumbled or clipped words go wrong: one bad token early
                // drags the rest of the phrase with it. Roughly 1.5–2× the
                // decode time; trivial on a GPU, still fine on CPU for the
                // short utterances dictation produces.
                builder = useBeam
                    ? builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(BeamSize))
                    : builder.WithGreedySamplingStrategy(g => g.WithBestOf(BeamSize));

                await using var processor = builder.Build();
                var sb = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(samples, ct))
                {
                    sb.Append(segment.Text);
                }
                // Unintelligible audio makes the model echo the prompt or loop
                // a phrase; both are caught here before anything is typed.
                return HallucinationFilter.Apply(
                    CleanNonSpeechMarkers(sb.ToString()), variantPrompt, vocabulary);
            }, ct);
        }
        finally
        {
            IsTranscribing = false;
            _lock.Release();
            StateChanged?.Invoke();
        }
    }

    private string DescribeRuntime()
    {
        try
        {
            return RuntimeOptions.LoadedLibrary switch
            {
                RuntimeLibrary.Vulkan => "GPU (Vulkan)",
                RuntimeLibrary.Cuda => "GPU (CUDA)",
                _ => "CPU",
            };
        }
        catch
        {
            return _gpuBroken || !SettingsStore.Instance.Settings.UseGpu ? "CPU" : "GPU/CPU";
        }
    }

    /// <summary>
    /// Strip whisper.cpp's non-speech annotations ([BLANK_AUDIO], [MUSIC], ♪,
    /// a lone "(wind blowing)") so silence never types garbage.
    /// </summary>
    internal static string CleanNonSpeechMarkers(string text)
    {
        var cleaned = Regex.Replace(text, @"\[[^\]]*\]", "");
        cleaned = cleaned.Replace("♪", "").Trim();
        if (Regex.IsMatch(cleaned, @"^\([^)]*\)$")) return "";
        return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
    }

    private static float[] ReadSamples(string wavPath)
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
