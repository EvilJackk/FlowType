using System.IO;
using FlowType.Core;

namespace FlowType.Engine;

/// <summary>
/// Curated whisper.cpp checkpoints, downloaded from Hugging Face
/// (ggerganov/whisper.cpp) straight into %APPDATA%\FlowType\models.
/// </summary>
public record WhisperModel(
    string Id,           // ggml variant, e.g. "small.en"
    string DisplayName,
    string Blurb,
    string SizeLabel,
    long MinBytes,       // sanity floor for download validation
    int MinRamGB,
    bool EnglishOnly)
{
    public string FileName => $"ggml-{Id}.bin";
    public string FilePath => Path.Combine(AppPaths.ModelsDir, FileName);
    public string DownloadUrl =>
        $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}";

    public bool IsDownloaded =>
        File.Exists(FilePath) && new FileInfo(FilePath).Length >= MinBytes;

    public string LanguageLabel => EnglishOnly ? "English only" : "Multilingual";
}

public static class ModelCatalog
{
    public static readonly IReadOnlyList<WhisperModel> All = new[]
    {
        new WhisperModel("tiny", "Tiny",
            "Instant, rough. Good for slow PCs.",
            "75 MB", 60_000_000, 2, EnglishOnly: false),
        new WhisperModel("base.en", "Base · English",
            "Snappy on any CPU, decent accuracy.",
            "142 MB", 120_000_000, 2, EnglishOnly: true),
        new WhisperModel("small.en", "Small · English",
            "The sweet spot for English dictation on CPU.",
            "466 MB", 400_000_000, 4, EnglishOnly: true),
        new WhisperModel("small", "Small · Multilingual",
            "The sweet spot for non-English dictation on CPU.",
            "466 MB", 400_000_000, 4, EnglishOnly: false),
        new WhisperModel("large-v3-turbo-q5_0", "Turbo · Quantized",
            "Near-flagship accuracy in a compact file. Shines with GPU.",
            "574 MB", 500_000_000, 6, EnglishOnly: false),
        new WhisperModel("large-v3-turbo", "Turbo · Full",
            "Best accuracy. Wants a GPU (or patience).",
            "1.6 GB", 1_400_000_000, 8, EnglishOnly: false),
    };

    public static WhisperModel? ById(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>Best starting point for this machine (RAM-based; user can switch anytime).</summary>
    public static WhisperModel Recommended()
    {
        var ramGB = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024 * 1024));
        return ramGB >= 8 ? ById("small.en")! : ById("base.en")!;
    }

    /// <summary>The best model that is already on disk, if any (largest wins).</summary>
    public static WhisperModel? BestDownloaded() =>
        All.Where(m => m.IsDownloaded).OrderByDescending(m => m.MinBytes).FirstOrDefault();
}
