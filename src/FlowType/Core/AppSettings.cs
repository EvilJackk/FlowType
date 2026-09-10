namespace FlowType.Core;

/// <summary>User preferences, persisted as JSON at %APPDATA%\FlowType\settings.json.</summary>
public class AppSettings
{
    // ----- Dictation -----

    /// <summary>
    /// Id of a HotkeyDefs entry (e.g. "RightCtrl", "CtrlWin"), or "Custom" to
    /// use <see cref="CustomVks"/> captured via the "Detect key" flow.
    /// </summary>
    public string Hotkey { get; set; } = "RightCtrl";

    /// <summary>Virtual-key codes captured by "Detect key" (chord = all held).</summary>
    public int[] CustomVks { get; set; } = Array.Empty<int>();

    /// <summary>Human label for the captured hotkey ("F13", "Mouse 5"…).</summary>
    public string CustomLabel { get; set; } = "";

    /// <summary>
    /// "smart" — hold to talk, quick-tap for hands-free (Wispr Flow behavior).
    /// "hold" — strictly push-to-talk. "toggle" — strictly press-to-start/stop.
    /// </summary>
    public string ActivationMode { get; set; } = "smart";

    /// <summary>Whisper language code, or "auto".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// English spelling convention: "us", "uk", or "off" to leave the model's
    /// output untouched. Also biases recognition toward that variant.
    /// </summary>
    public string EnglishVariant { get; set; } = "off";

    /// <summary>
    /// The last language a clip long enough to identify actually decoded as.
    /// Short push-to-talk takes inherit it, because whisper needs a 30-second
    /// window to identify a language and guesses badly on two seconds.
    /// </summary>
    public string LastDetectedLanguage { get; set; } = "en";

    // ----- AI model -----

    /// <summary>Model id from the catalog (e.g. "small.en"). Empty until onboarding.</summary>
    public string SelectedModel { get; set; } = "";

    /// <summary>Prefer the Vulkan GPU whisper runtime (falls back to CPU automatically).</summary>
    public bool UseGpu { get; set; } = true;

    /// <summary>
    /// Beam-search decoding (5 hypotheses) instead of greedy. Markedly better
    /// on unclear, quiet or rushed speech; costs roughly 1.5–2× decode time.
    /// </summary>
    public bool AccurateDecoding { get; set; } = true;

    // ----- Audio -----

    /// <summary>Capture device by name; empty follows the Windows default.</summary>
    public string MicDeviceName { get; set; } = "";

    public bool PlaySounds { get; set; } = true;

    // ----- Output -----

    /// <summary>Insert into the active app; false = copy to clipboard only.</summary>
    public bool AutoInsert { get; set; } = true;

    /// <summary>"paste" (Ctrl+V) or "type" (character-by-character SendInput).</summary>
    public string InsertMethod { get; set; } = "paste";

    public bool RestoreClipboard { get; set; } = true;

    /// <summary>
    /// Put one space after an inserted sentence so back-to-back dictations do
    /// not run together ("One.Two."). Suppressed automatically after addresses,
    /// links and anything that looks like code.
    /// </summary>
    public bool AppendTrailingSpace { get; set; } = true;

    // ----- Formatting -----

    public bool RemoveFillers { get; set; } = true;
    public bool ScratchThat { get; set; } = true;

    /// <summary>
    /// Correct near-misses of dictionary words even when no exact rule matches
    /// ("Armour Forger" → "Arma Reforger"). Compares sound as well as spelling,
    /// so one dictionary entry covers every way the model mangles the name.
    /// </summary>
    public bool FuzzyVocabulary { get; set; } = true;

    /// <summary>
    /// When you backspace over what FlowType just inserted and type the right
    /// thing instead, add that correction to the dictionary automatically. The
    /// keystrokes are only watched for a few seconds after an insertion and are
    /// never recorded — only the resulting dictionary entry is saved.
    /// </summary>
    public bool LearnCorrections { get; set; } = true;

    /// <summary>"new line" / "new paragraph" spoken commands.</summary>
    public bool LineCommands { get; set; } = true;

    /// <summary>"period", "comma", "question mark"… spoken commands (opt-in).</summary>
    public bool PunctuationCommands { get; set; } = false;

    public bool SmartTrailingPunctuation { get; set; } = true;

    // ----- Appearance / system -----

    /// <summary>Keep the flow bar visible at the bottom of the screen when idle.</summary>
    public bool ShowBarWhenIdle { get; set; } = true;

    /// <summary>Where the flow bar sits: "bottom", "top", or "hidden".</summary>
    public string BarPosition { get; set; } = "bottom";

    /// <summary>Save transcripts to Notes. Off = nothing is written to disk.</summary>
    public bool SaveHistory { get; set; } = true;

    /// <summary>Temporarily ignore the hotkey (for gaming, screen shares…).</summary>
    public bool DictationPaused { get; set; } = false;

    /// <summary>Hands-free safety cap; a forgotten mic stops itself.</summary>
    public int MaxRecordingMinutes { get; set; } = 5;

    /// <summary>
    /// Append one line per dictation to attempts.log: timings, audio
    /// measurements, outcome. No transcript text ever, so it is safe to share.
    /// On by default — its whole value is being on when the odd failure happens.
    /// </summary>
    public bool DiagnosticLog { get; set; } = true;

    public bool OnboardingComplete { get; set; } = false;

    /// <summary>Schema marker so future versions can migrate settings safely.</summary>
    public int SettingsVersion { get; set; } = 3;
}
