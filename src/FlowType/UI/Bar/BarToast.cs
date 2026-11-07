namespace FlowType.UI.Bar;

internal enum ToastKind
{
    Info,
    Success,
    Error,
}

/// <summary>
/// One transient message for the flow bar, classified from the session's
/// <c>Flash(message, isError)</c> event.
///
/// The session only says "here is some text, and is it bad news". The bar
/// decides how loud to be about it:
/// <list type="bullet">
/// <item><b>Mini</b> (3 s) — short good news or a nudge: "✓ 42 words",
///   "Canceled". The pill barely grows.</item>
/// <item><b>Standard</b> (6 s) — anything long or anything that went wrong.
///   Wraps to two lines at most; the pill grows to fit (≤ 420 DIP).</item>
/// </list>
/// A leading "✓" becomes a drawn check mark instead of a text glyph, so it
/// renders the same in every font. Hovering a toast holds it open.
/// </summary>
internal sealed record BarToast(string Text, ToastKind Kind, bool Mini, TimeSpan Duration)
{
    public static readonly TimeSpan MiniDuration = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan StandardDuration = TimeSpan.FromSeconds(6);

    /// <summary>Messages at or under this length (and not errors) are minis.</summary>
    private const int MiniMaxLength = 30;

    /// <summary>The message as the session sent it, for the in-recording caption.</summary>
    public string Original { get; init; } = "";

    /// <summary>Messages that are good news even without a check mark.</summary>
    private static readonly string[] SuccessPrefixes =
    {
        "Added to dictionary",
        "Learned from your edit",
        "Copied",
    };

    public static BarToast From(string message, bool isError)
    {
        var original = (message ?? "").Trim();
        var text = original;
        var kind = isError ? ToastKind.Error : ToastKind.Info;

        if (!isError && text.StartsWith('✓'))
        {
            kind = ToastKind.Success;
            text = text.TrimStart('✓').TrimStart();
        }
        else if (!isError && SuccessPrefixes.Any(p => text.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            kind = ToastKind.Success;
        }

        if (text.Length == 0) text = original;
        var mini = !isError && text.Length <= MiniMaxLength;
        return new BarToast(text, kind, mini, mini ? MiniDuration : StandardDuration)
        {
            Original = original,
        };
    }
}
