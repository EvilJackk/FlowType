using System.Text;
using System.Text.RegularExpressions;
using FlowType.Core;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace FlowType.Engine;

/// <summary>One chat turn for <see cref="LocalLlm"/>.</summary>
public readonly record struct ChatTurn(string Role, string Content);

/// <summary>
/// The model was still writing when it reached the token cap: what it produced
/// so far is the front of an answer, not an answer. LLamaSharp's executor ends
/// that case exactly like a finished one (the loop simply stops), so
/// <see cref="LocalLlm.CompleteAsync"/> raises this instead of handing back
/// text that would be pasted as if it were complete.
///
/// Deliberately carries no text: the partial output is the user's dictation
/// rewritten, and an exception message ends up in logs.
/// </summary>
public sealed class LlmTruncatedException : Exception
{
    public int MaxTokens { get; }

    public LlmTruncatedException(int maxTokens)
        : base($"The language model was cut off at its {maxTokens}-token limit before it finished.")
    {
        MaxTokens = maxTokens;
    }
}

/// <summary>
/// The optional on-device language model behind AI polish and Command Mode.
///
/// Wispr Flow does this step on its servers: the raw transcript, the text box
/// contents and a screenshot's OCR go up, formatted text comes back. FlowType
/// does it here, with a small open-weights model loaded through llama.cpp
/// (LLamaSharp). Nothing is sent anywhere.
///
/// llama.cpp and whisper.cpp ship separately named native libraries
/// (ggml-*.dll vs ggml-*-whisper.dll), so both load into one process; this was
/// checked on an RTX 4070 with both resident before it was wired in.
/// </summary>
public sealed class LocalLlm
{
    public static LocalLlm Instance { get; } = new();

    /// <summary>Room for the system prompt, the few-shot examples and a long dictation.</summary>
    private const uint ContextTokens = 4096;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _logGate = new();
    private readonly List<string> _nativeLog = new();
    private LLamaWeights? _weights;
    private ModelParams? _params;
    private bool _nativeConfigured;
    private bool _gpuBroken;

    public bool IsReady => _weights != null;
    public bool IsLoading { get; private set; }
    public string CurrentModelId { get; private set; } = "";
    public string RuntimeLabel { get; private set; } = "";
    public string? LastLoadError { get; private set; }

    /// <summary>Wall-clock of the most recent completion, for Insights and diagnostics.</summary>
    public long LastCompletionMs { get; private set; }

    public event Action? StateChanged;

    public IReadOnlyList<string> NativeLog
    {
        get { lock (_logGate) return _nativeLog.ToArray(); }
    }

    private LocalLlm() { }

    public LlmModel? CurrentModel => LlmCatalog.ById(CurrentModelId);

    /// <summary>Load whatever Settings names, if it is on disk. Never throws.</summary>
    public async Task InitializeAsync()
    {
        var id = SettingsStore.Instance.Settings.AiModel;
        var model = LlmCatalog.ById(id);
        if (model is not { IsDownloaded: true }) return;
        try { await LoadAsync(id); }
        catch { /* surfaced through LastLoadError */ }
    }

    /// <summary>
    /// The native library choice is process-wide and can only be made before
    /// the first llama.cpp call, so it is made exactly once.
    /// </summary>
    private void EnsureNativeConfigured()
    {
        if (_nativeConfigured) return;
        _nativeConfigured = true;
        var gpu = SettingsStore.Instance.Settings.UseGpu;
        NativeLibraryConfig.All.WithVulkan(gpu).WithAutoFallback(true);
        NativeLibraryConfig.All.WithLogCallback((level, message) =>
        {
            if (level < LLamaLogLevel.Info || string.IsNullOrWhiteSpace(message)) return;
            lock (_logGate)
            {
                if (_nativeLog.Count < 400) _nativeLog.Add($"[{level}] {message.TrimEnd()}");
            }
        });
    }

    public async Task LoadAsync(string modelId)
    {
        var model = LlmCatalog.ById(modelId)
            ?? throw new ArgumentException($"Unknown language model '{modelId}'.");
        if (!model.IsDownloaded)
        {
            throw new InvalidOperationException($"{model.DisplayName} is not downloaded yet.");
        }

        await _lock.WaitAsync();
        try
        {
            if (CurrentModelId == modelId && _weights != null) return;
            IsLoading = true;
            LastLoadError = null;
            StateChanged?.Invoke();

            // Release the old weights first: holding two models while the new
            // one loads is exactly how an 8 GB card runs out of memory.
            _weights?.Dispose();
            _weights = null;
            CurrentModelId = "";

            var (weights, p, label) = await Task.Run(() =>
            {
                EnsureNativeConfigured();
                var wantGpu = SettingsStore.Instance.Settings.UseGpu && !_gpuBroken;
                try
                {
                    return Load(model, wantGpu);
                }
                catch when (wantGpu)
                {
                    // The Vulkan device refused the weights (out of memory, a
                    // driver fault). The CPU path is slow but always works.
                    _gpuBroken = true;
                    return Load(model, gpu: false);
                }
            });

            _weights = weights;
            _params = p;
            CurrentModelId = modelId;
            RuntimeLabel = label;

            // The first completion compiles GPU pipelines for every batch shape
            // it meets — measured at 3-9 s on an RTX 4070. Pay that now, with the
            // real prompt shape, so the first dictation that uses AI does not.
            try
            {
                await CompleteAsync(AiWriter.WarmUpConversation(), 24, CancellationToken.None,
                    alreadyLocked: true);
            }
            catch { /* warm-up is an optimisation */ }
        }
        catch (Exception ex)
        {
            LastLoadError = ex.Message;
            throw;
        }
        finally
        {
            IsLoading = false;
            _lock.Release();
            StateChanged?.Invoke();
        }
    }

    private (LLamaWeights, ModelParams, string) Load(LlmModel model, bool gpu)
    {
        var p = new ModelParams(model.FilePath)
        {
            ContextSize = ContextTokens,
            GpuLayerCount = gpu ? 999 : 0,
            BatchSize = 512,
            Threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
        };
        var weights = LLamaWeights.LoadFromFile(p);
        var label = gpu && GpuOffloaded() ? "GPU (Vulkan)" : "CPU";
        return (weights, p, label);
    }

    /// <summary>llama.cpp reports how many layers it placed on the GPU; trust that, not the request.</summary>
    private bool GpuOffloaded()
    {
        foreach (var line in NativeLog)
        {
            var m = Regex.Match(line, @"offloaded (\d+)/(\d+) layers to GPU");
            if (m.Success) return int.Parse(m.Groups[1].Value) > 0;
        }
        return false;
    }

    /// <summary>
    /// Release the model if it is the loaded one. Awaits the lock rather than
    /// blocking on it: a completion can hold it for seconds, and this is called
    /// from the UI thread (Delete on the AI page).
    ///
    /// The id is checked again once the lock is held: while this waited, a
    /// load can have replaced the model, and the weights now in memory belong
    /// to a model the user did not ask to delete.
    /// </summary>
    public async Task UnloadIfCurrentAsync(string modelId)
    {
        if (CurrentModelId != modelId) return;
        await _lock.WaitAsync();
        var unloaded = false;
        try
        {
            if (CurrentModelId != modelId) return;
            _weights?.Dispose();
            _weights = null;
            _params = null;
            CurrentModelId = "";
            RuntimeLabel = "";
            unloaded = true;
        }
        finally
        {
            _lock.Release();
        }
        if (unloaded) StateChanged?.Invoke();
    }

    /// <summary>The stop strings of the ChatML template both catalog families use.</summary>
    private static readonly string[] StopStrings = { "<|im_end|>", "<|endoftext|>" };

    /// <summary>
    /// Run one chat completion and return the assistant's text. Deterministic
    /// (temperature 0) — this is an editor, not a writer of fiction.
    ///
    /// Only a finished answer is ever returned. LLamaSharp 0.27's
    /// <c>StatelessExecutor.InferAsync</c> ends quietly in two cases that are
    /// not finished answers: its loop runs while <c>i &lt; MaxTokens &amp;&amp;
    /// !ct.IsCancellationRequested</c>, so a cancelled token or the token cap
    /// simply stops the stream. Both used to come back here as ordinary text
    /// and were pasted as a complete rewrite. Now a stop by cancellation
    /// throws <see cref="OperationCanceledException"/> (which the callers map
    /// to "timed out" or "user cancelled" by asking their own token), and a
    /// stop at the cap throws <see cref="LlmTruncatedException"/>.
    ///
    /// All of the work runs on the thread pool. The executor's constructor
    /// builds and frees a 4096-token context, its first step builds another
    /// and tokenizes the prompt, and every sampled token is a continuation;
    /// the callers are the WPF dispatcher (polish, Command Mode, the load
    /// warm-up), which would otherwise run all of that between frames.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> fired before the answer was finished.</exception>
    /// <exception cref="LlmTruncatedException">The answer reached <paramref name="maxTokens"/> unfinished.</exception>
    public async Task<string> CompleteAsync(IReadOnlyList<ChatTurn> conversation, int maxTokens,
        CancellationToken ct, bool alreadyLocked = false)
    {
        if (!alreadyLocked) await _lock.WaitAsync(ct);
        try
        {
            var weights = _weights ?? throw new InvalidOperationException("No language model loaded.");
            var p = _params ?? throw new InvalidOperationException("No language model loaded.");
            var model = LlmCatalog.ById(CurrentModelId);
            var prompt = Render(conversation, model?.IsQwen3 == true);

            var (raw, produced, ms) = await Task.Run(async () =>
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var executor = new StatelessExecutor(weights, p);
                var infer = new InferenceParams
                {
                    MaxTokens = maxTokens,
                    AntiPrompts = StopStrings,
                    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f },
                };
                var sb = new StringBuilder();
                var count = 0;
                // Checked against the 0.27.0 IL: the loop yields exactly once
                // per sampled token (a token that is only part of a UTF-8
                // character yields ""), the token that completes a stop
                // string is yielded before the loop breaks, and an
                // end-of-generation token breaks without being yielded. So
                // this counts the tokens the cap counts.
                await foreach (var token in executor.InferAsync(prompt, infer, ct).ConfigureAwait(false))
                {
                    sb.Append(token);
                    count++;
                }
                return (sb.ToString(), count, watch.ElapsedMilliseconds);
            }, ct).ConfigureAwait(false);

            LastCompletionMs = ms;
            switch (ClassifyEnd(raw, produced, maxTokens, ct.IsCancellationRequested))
            {
                case CompletionEnd.Cancelled:
                    throw new OperationCanceledException(ct);
                case CompletionEnd.Truncated:
                    throw new LlmTruncatedException(maxTokens);
            }
            return Clean(raw);
        }
        finally
        {
            if (!alreadyLocked) _lock.Release();
        }
    }

    internal enum CompletionEnd { Finished, Cancelled, Truncated }

    /// <summary>
    /// Why the executor's stream ended. A stop string in the output, or fewer
    /// tokens than the cap with the token still live, is the model finishing
    /// (an end-of-generation token ends the loop without being yielded, so it
    /// always leaves the count under the cap). Otherwise a fired token means
    /// the loop was cut by cancellation, and a full count means it was cut by
    /// the cap. A stop string wins over a token that fired a moment later:
    /// that answer is complete. Pure, so --selftest can pin it.
    /// </summary>
    internal static CompletionEnd ClassifyEnd(string raw, int produced, int maxTokens, bool cancelled)
    {
        foreach (var stop in StopStrings)
        {
            if (raw.Contains(stop, StringComparison.Ordinal)) return CompletionEnd.Finished;
        }
        if (cancelled) return CompletionEnd.Cancelled;
        if (maxTokens > 0 && produced >= maxTokens) return CompletionEnd.Truncated;
        return CompletionEnd.Finished;
    }

    /// <summary>
    /// ChatML, which both catalog families use. Qwen3 is a hybrid reasoning
    /// model: "/no_think" plus an empty think block keeps it in the fast,
    /// non-reasoning mode the Qwen team documents for latency-sensitive use.
    /// </summary>
    internal static string Render(IReadOnlyList<ChatTurn> conversation, bool qwen3)
    {
        var sb = new StringBuilder();
        foreach (var turn in conversation)
        {
            sb.Append("<|im_start|>").Append(turn.Role).Append('\n');
            // Example answers carry the same empty think block a live answer
            // starts with, so the examples look exactly like what follows.
            if (qwen3 && turn.Role == "assistant") sb.Append("<think>\n\n</think>\n\n");
            sb.Append(turn.Content);
            if (qwen3 && turn.Role == "user") sb.Append(" /no_think");
            sb.Append("<|im_end|>\n");
        }
        sb.Append("<|im_start|>assistant\n");
        if (qwen3) sb.Append("<think>\n\n</think>\n\n");
        return sb.ToString();
    }

    private static readonly Regex ThinkBlock = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);

    internal static string Clean(string raw)
    {
        var text = ThinkBlock.Replace(raw, "");
        text = text.Replace("<|im_end|>", "").Replace("<|endoftext|>", "");
        // An unterminated think block (the model ran out of tokens mid-thought)
        // must never reach the user's document.
        var open = text.IndexOf("<think>", StringComparison.Ordinal);
        if (open >= 0) text = text[..open];
        return text.Trim();
    }
}
