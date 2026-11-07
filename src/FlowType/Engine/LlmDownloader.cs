using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using FlowType.Core;

namespace FlowType.Engine;

/// <summary>
/// Downloads a catalog language model and refuses to keep it unless both the
/// byte count and the SHA-256 match the pinned values. The hash is computed
/// while streaming, so verification costs no second pass over 2.5 GB.
/// </summary>
public sealed class LlmDownloader
{
    public static LlmDownloader Instance { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromHours(3) };

    private readonly Dictionary<string, CancellationTokenSource> _active = new();
    private readonly object _gate = new();

    /// <summary>(modelId, progress 0..1)</summary>
    public event Action<string, double>? ProgressChanged;

    /// <summary>(modelId, error message — null on success or cancel)</summary>
    public event Action<string, string?>? Completed;

    private LlmDownloader() { }

    public bool IsDownloading(string modelId)
    {
        lock (_gate) return _active.ContainsKey(modelId);
    }

    public async Task DownloadAsync(string modelId)
    {
        var model = LlmCatalog.ById(modelId)
            ?? throw new ArgumentException($"Unknown language model '{modelId}'.");

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

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long downloaded = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var target = File.Create(partialPath))
            {
                var buffer = new byte[1 << 17];
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    sha.AppendData(buffer, 0, read);
                    downloaded += read;
                    if (downloaded > model.Bytes)
                    {
                        throw new IOException("The download is larger than the published model — refusing it.");
                    }
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 150)
                    {
                        lastReport = DateTime.UtcNow;
                        ProgressChanged?.Invoke(modelId, (double)downloaded / model.Bytes);
                    }
                }
            }

            if (downloaded != model.Bytes)
            {
                throw new IOException("Download came up short — interrupted connection. Try again.");
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (actual != model.Sha256)
            {
                // A captive portal, a proxy rewriting content, or a tampered
                // mirror. Never hand unverified weights to the native loader.
                throw new IOException(
                    "The downloaded file does not match the published checksum, so it was discarded.");
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
    /// Re-hash a model already on disk (Settings → "Verify"). Slow on purpose:
    /// it reads every byte, which is the point.
    /// </summary>
    public static async Task<bool> VerifyAsync(LlmModel model, CancellationToken ct = default)
    {
        if (!File.Exists(model.FilePath)) return false;
        await using var stream = new FileStream(model.FilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, useAsync: true);
        if (stream.Length != model.Bytes) return false;
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant() == model.Sha256;
    }

    public void Cancel(string modelId)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(modelId, out var cts)) cts.Cancel();
        }
    }

    /// <summary>Unload (the weights are memory-mapped, so the file is locked while loaded), then delete.</summary>
    public async Task DeleteAsync(string modelId)
    {
        await LocalLlm.Instance.UnloadIfCurrentAsync(modelId);
        var model = LlmCatalog.ById(modelId);
        if (model != null && File.Exists(model.FilePath)) File.Delete(model.FilePath);
    }
}
