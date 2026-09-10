namespace FlowType.Core;

/// <summary>What kind of thing the user was dictating into.</summary>
public enum AppKind
{
    Other,
    AiPrompts,
    Email,
    WorkMessages,
    PersonalMessages,
    Documents,
    Code,
    Browsing,
}

/// <summary>
/// Groups process names into the handful of categories worth showing on the
/// Insights page.
///
/// Matching is on the whole process stem, never a substring: "1Password"
/// contains "word", "Barcode" contains "code", and "Teamspeak" contains "team".
/// A category is only ever claimed by an exact name here, so an unknown app
/// lands in <see cref="AppKind.Other"/> rather than in the wrong bucket.
/// </summary>
public static class AppCategory
{
    private static readonly Dictionary<string, AppKind> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Talking to a model
            ["claude"] = AppKind.AiPrompts,
            ["chatgpt"] = AppKind.AiPrompts,
            ["copilot"] = AppKind.AiPrompts,
            ["cursor"] = AppKind.AiPrompts,
            ["perplexity"] = AppKind.AiPrompts,
            ["lmstudio"] = AppKind.AiPrompts,
            ["ollama"] = AppKind.AiPrompts,

            // Email
            ["outlook"] = AppKind.Email,
            ["olk"] = AppKind.Email,
            ["thunderbird"] = AppKind.Email,
            ["mailspring"] = AppKind.Email,
            ["hey"] = AppKind.Email,
            ["superhuman"] = AppKind.Email,

            // Work chat
            ["slack"] = AppKind.WorkMessages,
            ["teams"] = AppKind.WorkMessages,
            ["ms-teams"] = AppKind.WorkMessages,
            ["zoom"] = AppKind.WorkMessages,
            ["webex"] = AppKind.WorkMessages,

            // Personal chat
            ["discord"] = AppKind.PersonalMessages,
            ["whatsapp"] = AppKind.PersonalMessages,
            ["telegram"] = AppKind.PersonalMessages,
            ["signal"] = AppKind.PersonalMessages,
            ["messenger"] = AppKind.PersonalMessages,

            // Writing
            ["winword"] = AppKind.Documents,
            ["notepad"] = AppKind.Documents,
            ["wordpad"] = AppKind.Documents,
            ["obsidian"] = AppKind.Documents,
            ["notion"] = AppKind.Documents,
            ["onenote"] = AppKind.Documents,
            ["evernote"] = AppKind.Documents,
            ["scrivener"] = AppKind.Documents,
            ["typora"] = AppKind.Documents,

            // Code
            ["code"] = AppKind.Code,
            ["devenv"] = AppKind.Code,
            ["rider64"] = AppKind.Code,
            ["idea64"] = AppKind.Code,
            ["pycharm64"] = AppKind.Code,
            ["sublime_text"] = AppKind.Code,
            ["notepad++"] = AppKind.Code,
            ["windowsterminal"] = AppKind.Code,
            ["powershell"] = AppKind.Code,
            ["cmd"] = AppKind.Code,

            // The web
            ["chrome"] = AppKind.Browsing,
            ["firefox"] = AppKind.Browsing,
            ["msedge"] = AppKind.Browsing,
            ["brave"] = AppKind.Browsing,
            ["opera"] = AppKind.Browsing,
            ["vivaldi"] = AppKind.Browsing,
            ["arc"] = AppKind.Browsing,
        };

    public static AppKind Classify(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return AppKind.Other;
        return Known.GetValueOrDefault(processName.Trim(), AppKind.Other);
    }

    public static string Label(AppKind kind) => kind switch
    {
        AppKind.AiPrompts => "AI prompts",
        AppKind.Email => "emails",
        AppKind.WorkMessages => "work messages",
        AppKind.PersonalMessages => "personal messages",
        AppKind.Documents => "documents",
        AppKind.Code => "code and terminals",
        AppKind.Browsing => "the web",
        _ => "other apps",
    };
}
