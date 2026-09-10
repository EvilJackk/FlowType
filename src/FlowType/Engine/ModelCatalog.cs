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
            "The fast pick for a GPU: far better than Small on unclear speech, and quickest of the big models. Makes about three times Large's errors on mumbling.",
            "1.6 GB", 1_400_000_000, 8, EnglishOnly: false),
        new WhisperModel("large-v3-q5_0", "Large · Maximum",
            "The accuracy pick. Best on mumbled speech in our tests — about a third of Turbo's errors — for roughly twice the wait. Wants a strong GPU.",
            "1.1 GB", 950_000_000, 8, EnglishOnly: false),
    };

    public static WhisperModel? ById(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Best starting point for this machine; the user can switch anytime.
    /// A discrete GPU runs Turbo faster than a CPU runs Small, and Turbo is
    /// a different league on quiet or mumbled speech — so GPU machines are
    /// steered there. CPU-only machines stay on the RAM-based pick.
    /// </summary>
    public static WhisperModel Recommended()
    {
        if (SettingsStore.Instance.Settings.UseGpu && GpuInfo.HasDiscreteGpu)
        {
            // Our own hard-set bench: on mumbled speech large-v3 makes about a
            // third of Turbo's errors, at roughly twice the wait on a real GPU.
            // Recommending Turbo here contradicted the catalog's own blurb and
            // steered people away from the thing they came for.
            var ram = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024 * 1024));
            return ram >= 8 ? ById("large-v3-q5_0")! : ById("large-v3-turbo")!;
        }
        var ramGB = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024 * 1024));
        return ramGB >= 8 ? ById("small.en")! : ById("base.en")!;
    }

    /// <summary>The best model that is already on disk, if any (largest wins).</summary>
    public static WhisperModel? BestDownloaded() =>
        All.Where(m => m.IsDownloaded).OrderByDescending(m => m.MinBytes).FirstOrDefault();
}
