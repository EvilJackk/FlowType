using System.Windows.Threading;
using FlowType.Core;
using FlowType.Input;

namespace FlowType.Session;

/// <summary>A correction the user made by hand, ready to become a dictionary rule.</summary>
public readonly record struct LearnedCorrection(string Heard, string Write);

/// <summary>
/// Learns from the fix you make yourself.
///
/// When FlowType has just inserted text and you immediately backspace over the
/// end of it and type what you actually meant, that is the most precise signal
/// there is about how the model mishears you — better than any rule you could
/// think to write in advance, because it is a real example. This watches for
/// exactly that gesture and turns it into a dictionary entry, then says so.
///
/// It only works from the end of the insertion backwards, because that is the
/// only edit whose effect on the text can be known without reading the target
/// application's contents. Anything that moves the caret somewhere we cannot
/// follow — an arrow key, Home/End, a mouse click, changing window, starting
/// another dictation — disarms the watcher rather than guessing.
///
/// Nothing is recorded anywhere while it is armed: the keystrokes live in a
/// small buffer in memory for at most <see cref="Window"/>, and the only thing
/// that ever reaches disk is the dictionary entry your own correction created.
/// </summary>
public sealed class CorrectionWatcher
{
    /// <summary>How long after an insertion a correction still counts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <summary>Quiet time after the last keystroke before the edit is judged finished.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(1200);

    /// <summary>Longest term worth learning; past this it is a rewrite, not a correction.</summary>
    private const int MaxTermLength = 40;

    private const int MinTermLength = 3;
    private const int MaxWords = 4;

    public static CorrectionWatcher Instance { get; } = new();

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private string _inserted = "";
    private int _backspaces;
    private readonly System.Text.StringBuilder _typed = new();
    private DateTime _armedAt;
    private DateTime _lastKey;
    private IntPtr _window;
    private bool _armed;

    /// <summary>Raised when a correction has been learned, for the flow bar.</summary>
    public event Action<LearnedCorrection>? Learned;

    private CorrectionWatcher()
    {
        _timer.Tick += (_, _) => Evaluate(force: false);
    }

    public void Attach(HotkeyManager hotkeys)
    {
        hotkeys.KeyTyped += OnKey;
    }

    /// <summary>Start watching after FlowType put <paramref name="text"/> into the focused app.</summary>
    public void Arm(string text)
    {
        Disarm();
        if (!SettingsStore.Instance.Settings.LearnCorrections) return;
        if (string.IsNullOrWhiteSpace(text)) return;

        _inserted = text.TrimEnd();
        _backspaces = 0;
        _typed.Clear();
        _armedAt = DateTime.UtcNow;
        _lastKey = _armedAt;
        _window = NativeMethods.GetForegroundWindow();
        _armed = true;
        // Mouse state is sampled from here on; prime the "since last call" bit
        // so a click from before we armed doesn't count.
        NativeMethods.IsKeyDown(NativeMethods.VK_LBUTTON);
        NativeMethods.IsKeyDown(NativeMethods.VK_RBUTTON);
        _timer.Start();
    }

    public void Disarm()
    {
        _armed = false;
        _timer.Stop();
        _inserted = "";
        _backspaces = 0;
        _typed.Clear();
    }

    private void OnKey(int vk, char character)
    {
        if (!_armed) return;

        // Anything that can move the caret out from under us ends the watch.
        if (IsCaretMover(vk) || NativeMethods.GetForegroundWindow() != _window)
        {
            Evaluate(force: true);
            Disarm();
            return;
        }

        _lastKey = DateTime.UtcNow;

        if (vk == NativeMethods.VK_BACK)
        {
            // Backspacing after typing means the user is editing their own
            // correction, not ours — take it off what they typed.
            if (_typed.Length > 0) _typed.Length--;
            else _backspaces++;
            return;
        }

        if (character != '\0') _typed.Append(character);
    }

    /// <summary>Keys after which we can no longer say where the caret is.</summary>
    private static bool IsCaretMover(int vk) => vk is
        0x25 or 0x26 or 0x27 or 0x28      // arrows
        or 0x24 or 0x23                   // Home, End
        or 0x21 or 0x22                   // PageUp, PageDown
        or 0x0D or 0x09                   // Enter, Tab
        or 0x1B                           // Esc
        or 0x2E;                          // Delete (forward)

    private void Evaluate(bool force)
    {
        if (!_armed) return;

        var now = DateTime.UtcNow;
        var expired = now - _armedAt > Window;
        var clicked = NativeMethods.WasClicked();

        if (clicked || expired)
        {
            // A click may have moved the caret before the typing, so what we
            // have can no longer be trusted — drop it rather than guess.
            Disarm();
            return;
        }

        if (!force && now - _lastKey < Settle) return;
        if (_backspaces == 0 || _typed.Length == 0) return;

        var correction = Diff(_inserted, _backspaces, _typed.ToString());
        Disarm();
        if (correction == null) return;

        if (DictionaryStore.Instance.AddLearned(correction.Value.Heard, correction.Value.Write))
        {
            Learned?.Invoke(correction.Value);
        }
    }

    /// <summary>
    /// Work out what was replaced with what.
    ///
    /// The caret sits at the end of the inserted text, so N backspaces removed
    /// its last N characters. If that landed in the middle of a word, the part
    /// still in the document is a prefix of both the old and the new word — so
    /// it is put back on both sides, and backspacing just the wrong half of a
    /// word still produces a whole-word rule.
    /// </summary>
    internal static LearnedCorrection? Diff(string inserted, int backspaces, string typed)
    {
        if (backspaces <= 0 || backspaces > inserted.Length) return null;   // ate our own text and more
        if (string.IsNullOrWhiteSpace(typed)) return null;

        var kept = inserted[..^backspaces];
        var removed = inserted[^backspaces..];

        // The partial word left behind belongs to both sides.
        var boundary = kept.LastIndexOfAny(new[] { ' ', '\t', '\n', '\r' });
        var prefix = boundary < 0 ? kept : kept[(boundary + 1)..];

        var heard = (prefix + removed).Trim();
        var write = (prefix + typed).Trim();

        if (!IsLearnable(heard) || !IsLearnable(write)) return null;
        if (heard.Equals(write, StringComparison.OrdinalIgnoreCase)) return null;

        return new LearnedCorrection(heard, write);
    }

    /// <summary>
    /// A term, not a sentence and not a fragment: a few words, some letters in
    /// it, and nothing that belongs to a URL or an address.
    /// </summary>
    private static bool IsLearnable(string term)
    {
        if (term.Length is < MinTermLength or > MaxTermLength) return false;
        if (!term.Any(char.IsLetter)) return false;
        if (term.Contains('@') || term.Contains("://") || term.Contains('/')) return false;
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is >= 1 and <= MaxWords;
    }
}
