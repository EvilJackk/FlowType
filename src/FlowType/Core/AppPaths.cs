using System.IO;

namespace FlowType.Core;

/// <summary>Every file-system location FlowType uses, under %APPDATA%\FlowType.</summary>
public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlowType");

    /// <summary>Downloaded ggml Whisper checkpoints (ggml-*.bin).</summary>
    public static string ModelsDir { get; } = Path.Combine(DataDir, "models");

    /// <summary>Short-lived WAVs while a dictation is in flight.</summary>
    public static string RecordingsDir { get; } = Path.Combine(DataDir, "recordings");

    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
    public static string NotesFile { get; } = Path.Combine(DataDir, "notes.json");
    public static string DictionaryFile { get; } = Path.Combine(DataDir, "dictionary.json");
    public static string StatsFile { get; } = Path.Combine(DataDir, "stats.json");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(RecordingsDir);
    }
}
