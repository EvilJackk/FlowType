using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FlowType.Core;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FlowType.Engine;

/// <summary>
/// Local speech-to-text via Whisper.net (whisper.cpp). Prefers the Vulkan GPU
/// runtime when enabled and silently falls back to CPU if the GPU path can't
/// load or crashes (a known failure mode on some driver stacks). The loaded
/// model is cached; a processor is built per utterance so language and the
/// vocabulary-bias prompt can change between dictations.
/// </summary>
public sealed class Transcriber
{
    public static Transcriber Instance { get; } = new();

    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _runtimeOrderSet;
    private bool _gpuBroken;

    public bool IsReady => _factory != null;
    public bool IsLoading { get; private set; }
    public bool IsTranscribing { get; private set; }
    public string CurrentModelId { get; private set; } = "";

    /// <summary>Human description of the active compute path, for the UI.</summary>
    public string RuntimeLabel { get; private set; } = "";

    public event Action? StateChanged;

    private Transcriber() { }

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
                try
                {
                    return WhisperFactory.FromPath(model.FilePath);
                }
                catch when (!_gpuBroken && SettingsStore.Instance.Settings.UseGpu)
                {
                    // GPU runtime failed to come up — retry the load on CPU only.
                    // (Only effective if no native lib was loaded yet.)
                    _gpuBroken = true;
                    RuntimeOptions.RuntimeLibraryOrder =
                        new List<RuntimeLibrary> { RuntimeLibrary.Cpu };
                    return WhisperFactory.FromPath(model.FilePath);
                }
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
    /// </summary>
    public async Task<string> TranscribeAsync(
        string wavPath, string language, IReadOnlyList<string> vocabulary,
        CancellationToken ct = default)
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
                var samples = ReadSamples(wavPath);
                if (samples.Length == 0) return "";

                var builder = factory.CreateBuilder();

                var model = ModelCatalog.ById(CurrentModelId);
                var effectiveLanguage = model?.EnglishOnly == true ? "en" : language;
                builder = effectiveLanguage == "auto"
                    ? builder.WithLanguageDetection()
                    : builder.WithLanguage(effectiveLanguage);

                // Initial prompt conditions the decoder: the English variant's
                // sample text biases spelling, the user's dictionary words make
                // uncommon names/terms far likelier to be recognized.
                var promptParts = new List<string>();
                var variantPrompt = EnglishVariant.RecognitionPrompt(
                    SettingsStore.Instance.Settings.EnglishVariant);
                if (variantPrompt != null) promptParts.Add(variantPrompt);
                if (vocabulary.Count > 0) promptParts.Add(string.Join(", ", vocabulary.Take(24)));
                if (promptParts.Count > 0)
                {
                    builder = builder.WithPrompt(string.Join(" ", promptParts));
                }

                await using var processor = builder.Build();
                var sb = new StringBuilder();
                await foreach (var segment in processor.ProcessAsync(samples, ct))
                {
                    sb.Append(segment.Text);
                }
                return CleanNonSpeechMarkers(sb.ToString());
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
