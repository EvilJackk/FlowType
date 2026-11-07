using System.IO;

namespace FlowType.Core;

/// <summary>Every file-system location FlowType uses, under %APPDATA%\FlowType.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowType");

    /// <summary>Downloaded ggml Whisper checkpoints (ggml-*.bin).</summary>
    public static string ModelsDir { get; } = Path.Combine(DataDir, "models");

    /// <summary>Downloaded GGUF language models for the optional AI layer.</summary>
    public static string LlmDir { get; } = Path.Combine(DataDir, "llm");

    /// <summary>Short-lived WAVs while a dictation is in flight.</summary>
    public static string RecordingsDir { get; } = Path.Combine(DataDir, "recordings");

    /// <summary>
    /// Takes kept on purpose (Settings → Privacy → keep recordings), so a
    /// dictation can be re-transcribed from History. Pruned by age.
    /// </summary>
    public static string AudioArchiveDir { get; } = Path.Combine(DataDir, "audio");

    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
    public static string NotesFile { get; } = Path.Combine(DataDir, "notes.json");
    public static string DictionaryFile { get; } = Path.Combine(DataDir, "dictionary.json");
    public static string StatsFile { get; } = Path.Combine(DataDir, "stats.json");

    /// <summary>One JSON line per dictation — measurements only, never text.</summary>
    public static string AttemptLog { get; } = Path.Combine(DataDir, "attempts.log");

    /// <summary>
    /// "Learn from your writing" bookkeeping: when it last ran, benchmark
    /// numbers, how many suggestions wait for review, and hashed "don't suggest
    /// again" keys. Never any dictation text or term.
    /// </summary>
    public static string LearningFile { get; } = Path.Combine(DataDir, "learning.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(LlmDir);
        Directory.CreateDirectory(RecordingsDir);
        Directory.CreateDirectory(AudioArchiveDir);
    }
}
