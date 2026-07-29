namespace FlowType.Core;

/// <summary>Loads and persists <see cref="AppSettings"/>; raises Changed on save.</summary>
public sealed class SettingsStore
{
    public static SettingsStore Instance { get; } = new();

    private readonly object _gate = new();

    public AppSettings Settings { get; private set; }

    public event Action? Changed;

    private SettingsStore()
    {
        Settings = Load();
    }

    public void Save()
    {
        lock (_gate)
        {
            JsonFile.Write(AppPaths.SettingsFile, Settings);
        }
        Changed?.Invoke();
    }

    private static AppSettings Load() =>
        JsonFile.Read<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
}
