using System.IO;
using System.Text.Json;

namespace FlowType.Core;

/// <summary>
/// Crash-safe JSON persistence. Writes go to a temp file that is flushed to
/// disk and then atomically swapped in, so a power cut or kill mid-write can
/// never leave a half-written settings/notes file behind. A `.bak` of the
/// previous good copy is kept and used automatically if the main file is
/// unreadable.
/// </summary>
public static class JsonFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Write<T>(string path, T value)
    {
        AppPaths.EnsureCreated();
        var tempPath = path + ".tmp";
        var backupPath = path + ".bak";

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
            FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Options);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // Replace keeps a backup and is atomic on NTFS.
            File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    public static T? Read<T>(string path) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var json = File.ReadAllText(candidate);
                if (string.IsNullOrWhiteSpace(json)) continue;
                var value = JsonSerializer.Deserialize<T>(json);
                if (value != null) return value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Read failed for {candidate}: {ex.Message}");
            }
        }
        return null;
    }
}
