using System.IO;
using System.Net.Http;
using FlowType.Core;

namespace FlowType.Engine;

/// <summary>
/// Streams ggml checkpoints from Hugging Face with progress; ".partial" until
/// the size sanity check passes. One download per model at a time.
/// </summary>
public sealed class ModelDownloader
{
    public static ModelDownloader Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(2) };

    private readonly Dictionary<string, CancellationTokenSource> _active = new();
    private readonly object _gate = new();

    /// <summary>(modelId, progress 0..1)</summary>
    public event Action<string, double>? ProgressChanged;

    /// <summary>(modelId, error message — null on success or cancel)</summary>
    public event Action<string, string?>? Completed;

    private ModelDownloader() { }

    public bool IsDownloading(string modelId)
    {
        lock (_gate) return _active.ContainsKey(modelId);
    }

    public async Task DownloadAsync(string modelId)
    {
        var model = ModelCatalog.ById(modelId)
            ?? throw new ArgumentException($"Unknown model '{modelId}'.");

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_active.ContainsKey(modelId)) return;
            cts = new CancellationTokenSource();
            _active[modelId] = cts;
        }

        AppPaths.EnsureCreated();
        var partialPath = model.FilePath + ".partial";
        string? error = null;

        try
        {
            using var response = await Http.GetAsync(
                model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? model.MinBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var target = File.Create(partialPath))
            {
                var buffer = new byte[1 << 16];
                long downloaded = 0;
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    downloaded += read;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
                    {
                        lastReport = DateTime.UtcNow;
                        ProgressChanged?.Invoke(modelId,
                            Math.Min(1.0, (double)downloaded / totalBytes));
                    }
                }
            }

            if (new FileInfo(partialPath).Length < model.MinBytes)
            {
                throw new IOException("Download came up short — corrupt or interrupted.");
            }
            if (!HasGgmlMagic(partialPath))
            {
                // Not a model file: a captive-portal login page, an HTML error,
                // or a truncated/tampered transfer. Never hand it to the engine.
                throw new IOException(
                    "Downloaded file is not a valid Whisper model — check your connection and retry.");
            }

            File.Move(partialPath, model.FilePath, overwrite: true);
            ProgressChanged?.Invoke(modelId, 1.0);
        }
        catch (OperationCanceledException)
        {
            // user cancel
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            lock (_gate) _active.Remove(modelId);
            if (File.Exists(partialPath))
            {
                try { File.Delete(partialPath); } catch { }
            }
            cts.Dispose();
            Completed?.Invoke(modelId, error);
        }
    }

    /// <summary>
    /// ggml checkpoints start with the 32-bit magic 0x67676D6C ("ggml",
    /// little-endian on disk). Cheap structural check that the bytes we got are
    /// really a model.
    /// </summary>
    private static bool HasGgmlMagic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[4];
            if (stream.Read(header, 0, 4) != 4) return false;
            return BitConverter.ToUInt32(header, 0) == 0x67676D6C;
        }
        catch
        {
            return false;
        }
    }

    public void Cancel(string modelId)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(modelId, out var cts)) cts.Cancel();
        }
    }

    public void Delete(string modelId)
    {
        Transcriber.Instance.UnloadIfCurrent(modelId);
        var model = ModelCatalog.ById(modelId);
        if (model != null && File.Exists(model.FilePath)) File.Delete(model.FilePath);
    }
}
