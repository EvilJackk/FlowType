using System.IO;
using FlowType.Core;

namespace FlowType.Engine;

/// <summary>
/// A local language model for the optional AI layer (polish and Command
/// Mode). Everything here runs on this PC; the file is downloaded once from
/// the model publisher's own Hugging Face repository and checked against a
/// pinned SHA-256 before it is ever loaded.
/// </summary>
public record LlmModel(
    string Id,
    string DisplayName,
    string Blurb,
    string SizeLabel,
    string Repository,     // Hugging Face repo, publisher-owned
    string FileName,
    long Bytes,            // exact size of the pinned file
    string Sha256,         // lowercase hex of the pinned file
    int MinVramGB,
    bool IsQwen3)
{
    public string FilePath => Path.Combine(AppPaths.LlmDir, FileName);

    public string DownloadUrl => $"https://huggingface.co/{Repository}/resolve/main/{FileName}";

    /// <summary>Present at the pinned size. The hash is checked at download time.</summary>
    public bool IsDownloaded =>
        File.Exists(FilePath) && new FileInfo(FilePath).Length == Bytes;
}

public static class LlmCatalog
{
    // Both are Apache-2.0 and published by the Qwen team itself. The hashes are
    // the LFS object ids Hugging Face reports for these exact files (checked
    // 2026-10-19 against huggingface.co/api/models/<repo>?blobs=true).
    public static readonly IReadOnlyList<LlmModel> All = new[]
    {
        new LlmModel("qwen3-4b", "Qwen3 4B",
            "The recommended writer. Resolves self-corrections (\"Thursday, no wait, Friday\") and reformats lists; about half a second per dictation on a mid-range GPU.",
            "2.5 GB", "Qwen/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf",
            2_497_280_256, "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
            MinVramGB: 6, IsQwen3: true),
        new LlmModel("qwen2.5-1.5b", "Qwen2.5 1.5B · Lite",
            "Small and quick, for laptops and CPU-only machines. Cleans punctuation and fillers well; weaker on self-corrections.",
            "1.1 GB", "Qwen/Qwen2.5-1.5B-Instruct-GGUF", "qwen2.5-1.5b-instruct-q4_k_m.gguf",
            1_117_320_736, "6a1a2eb6d15622bf3c96857206351ba97e1af16c30d7a74ee38970e434e9407e",
            MinVramGB: 2, IsQwen3: false),
    };

    public static LlmModel? ById(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>Best pick for this machine.</summary>
    public static LlmModel Recommended() =>
        SettingsStore.Instance.Settings.UseGpu && GpuInfo.HasDiscreteGpu
            ? ById("qwen3-4b")!
            : ById("qwen2.5-1.5b")!;

    public static LlmModel? BestDownloaded() =>
        All.Where(m => m.IsDownloaded).OrderByDescending(m => m.Bytes).FirstOrDefault();
}
