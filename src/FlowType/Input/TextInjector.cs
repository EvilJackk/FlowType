using System.Diagnostics;
using System.Windows;
using FlowType.Core;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;

namespace FlowType.Input;

/// <summary>
/// Puts the transcript into the focused app. Default path: clipboard + Ctrl+V
/// with clipboard restore. Fallback path for paste-hostile fields: character
/// typing via SendInput unicode (newlines become Shift+Enter so chat boxes
/// don't submit mid-text).
/// </summary>
public static class TextInjector
{
    public static Task PasteAsync(string text, bool restoreClipboard) =>
        PasteAsync(text, restoreClipboard, waitUntilRead: false);

    /// <param name="waitUntilRead">
    /// Return only once the target has read the text (or the transaction timed
    /// out) — needed before pressing Enter or pasting the next chunk, which
    /// would otherwise overtake a paste that is merely queued.
    /// </param>
    public static async Task PasteAsync(string text, bool restoreClipboard, bool waitUntilRead)
    {
        // Preferred path: publish a clipboard promise and restore only once the
        // target has actually read it (see ReliablePaste). Nothing to "settle"
        // before the chord either, which takes ~120 ms out of every dictation.
        var settled = waitUntilRead ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (await ReliablePaste.PasteAsync(text, restoreClipboard, sendChord: true, settled))
        {
            if (settled != null)
            {
                try { await settled.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (TimeoutException) { }
            }
            return;
        }

        // Fallback for the rare case the transaction cannot start (another app
        // holding the clipboard open, a window-class failure): the old
        // fixed-delay dance, which is racy but better than not pasting.
        var previous = OnUIThread(TryGetClipboardText);
        OnUIThread(() => TrySetClipboardText(text));

        await Task.Delay(120);          // let the clipboard settle
        NativeMethods.SendCtrlV();

        if (restoreClipboard && previous != null)
        {
            await Task.Delay(500);      // let the target app consume the paste
            OnUIThread(() =>
            {
                if (TryGetClipboardText() == text) TrySetClipboardText(previous);
            });
        }
    }

    public static async Task TypeAsync(string text)
    {
        var chunk = 0;
        foreach (var c in text)
        {
            // Keep the polling watchdog muted for the whole type-out, not just
            // the 250 ms each Send() buys.
            InjectionGuard.Extend(400);
            if (c == '\n')
            {
                NativeMethods.SendShiftEnter();
            }
            else if (c != '\r')
            {
                NativeMethods.Send(
                    NativeMethods.UnicodeInput(c, keyUp: false),
                    NativeMethods.UnicodeInput(c, keyUp: true));
            }
            if (++chunk % 20 == 0) await Task.Delay(4);  // pacing so slow apps keep up
        }
    }

    public static void CopyOnly(string text) => OnUIThread(() => TrySetClipboardText(text));

    // ----- 2.0 additions -----

    /// <summary>Console hosts and terminal emulators, by process name.</summary>
    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowsterminal", "cmd", "powershell", "pwsh", "conhost", "openconsole",
        "wezterm-gui", "wezterm", "alacritty", "hyper", "kitty", "warp", "tabby", "mintty",
        "putty", "kitty_portable", "conemu", "conemu64", "mobaxterm", "termius", "securecrt",
        "wt", "fluentterminal", "rio", "ghostty",
    };

    public static bool IsTerminal(string processName) => Terminals.Contains(processName);

    /// <summary>
    /// Whether the foreground window is a terminal running a coding agent
    /// (Claude Code, Codex, Gemini CLI…). Those TUIs collapse a large paste into
    /// a "[Pasted text +N lines]" placeholder, so long dictations are pasted
    /// there in pieces instead — Wispr Flow does the same for Claude Code and
    /// Codex (≤600 characters and ≤2 line breaks per piece).
    /// </summary>
    public static bool IsCodingAgentTerminal(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var process = ProcessNameOf(hwnd);
        if (!IsTerminal(process)) return false;
        var title = NativeMethods.WindowTitle(hwnd).ToLowerInvariant();
        return title.Contains("claude") || title.Contains("codex") || title.Contains("gemini")
            || title.Contains("aider") || title.Contains("opencode");
    }

    private const int ChunkChars = 600;
    private const int ChunkNewlines = 2;

    /// <summary>Split at line or sentence ends so no piece exceeds the coding-agent limits.</summary>
    internal static IReadOnlyList<string> ChunkForTerminal(string text)
    {
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        var newlines = 0;
        foreach (var piece in System.Text.RegularExpressions.Regex.Split(text, @"(?<=\n)|(?<=[.!?] )"))
        {
            if (piece.Length == 0) continue;
            var pieceNewlines = piece.Count(c => c == '\n');
            if (current.Length > 0 && (current.Length + piece.Length > ChunkChars || newlines + pieceNewlines > ChunkNewlines))
            {
                chunks.Add(current.ToString());
                current.Clear();
                newlines = 0;
            }
            // A single sentence longer than the limit is cut at a space.
            var rest = piece;
            while (rest.Length > ChunkChars)
            {
                var cut = rest.LastIndexOf(' ', ChunkChars);
                if (cut <= 0) cut = ChunkChars;
                chunks.Add(rest[..cut]);
                rest = rest[cut..];
            }
            current.Append(rest);
            newlines += pieceNewlines;
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    /// <summary>
    /// Paste in pieces into a coding-agent terminal. Each piece waits until
    /// the previous one has actually been read, and the user's clipboard is
    /// saved once up front and put back once at the end — per-chunk restores
    /// would each save the *previous chunk* as "the user's clipboard".
    /// </summary>
    public static async Task PasteChunkedAsync(string text, bool restoreClipboard)
    {
        var chunks = ChunkForTerminal(text);
        var previous = restoreClipboard ? ReliablePaste.ReadText() : null;
        foreach (var chunk in chunks)
        {
            await PasteAsync(chunk, restoreClipboard: false, waitUntilRead: true);
        }
        // Only if the clipboard still holds our last piece: a copy the user
        // made in the meantime wins.
        if (previous != null && chunks.Count > 0 && ReliablePaste.ReadText() == chunks[^1])
        {
            ReliablePaste.RestorePrivate(previous);
        }
    }

    /// <summary>
    /// The "press enter" voice command. The caller pastes with
    /// <c>waitUntilRead</c> first, so the Enter can never arrive before the
    /// text; the short pause covers apps that read the clipboard and then
    /// insert asynchronously. Esc during the pause still cancels.
    /// </summary>
    public static async Task PressEnterAsync(CancellationToken ct = default)
    {
        await Task.Delay(120, ct);
        ct.ThrowIfCancellationRequested();
        NativeMethods.SendEnter();
    }

    /// <summary>
    /// If the user clicked FlowType's own bar or window while talking, the
    /// keyboard focus is ours and a paste would land nowhere. Hand focus back
    /// to the window the dictation started in. Wispr's helper stores the
    /// focused app at the start of a dictation for the same reason.
    /// </summary>
    public static void RefocusIfStolen(IntPtr target)
    {
        try
        {
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return;
            if (NativeMethods.IsOwnProcessWindow(target)) return;
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == target || !NativeMethods.IsOwnProcessWindow(foreground)) return;
            NativeMethods.SetForegroundWindow(target);
            Thread.Sleep(60);
        }
        catch
        {
            // Focus juggling is best effort; the paste still goes somewhere sensible.
        }
    }

    /// <summary>What the Ctrl+C fallback found.</summary>
    public enum CopyProbe { Selection, NothingSelected, Unsafe }

    /// <summary>
    /// Command Mode's fallback when UI Automation cannot see a selection: ask
    /// the app to copy it (Ctrl+C), read the clipboard, and put the user's
    /// clipboard back whenever the copy changed it (text or not).
    ///
    /// Only sent to a focused edit or document control — Ctrl+C in a terminal
    /// (or anything else unknown) interrupts the program running there. A copy
    /// that ends with a line break is how editors copy the whole current line
    /// when nothing is selected, so it does not count as a selection.
    /// </summary>
    public static async Task<(CopyProbe Result, string? Text)> GetSelectedTextViaCopyAsync(
        string focusedControlType, int timeoutMs = 600)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || NativeMethods.IsOwnProcessWindow(foreground)) return (CopyProbe.Unsafe, null);
        if (IsTerminal(ProcessNameOf(foreground))) return (CopyProbe.Unsafe, null);
        if (!IsEditLikeControl(focusedControlType)) return (CopyProbe.Unsafe, null);

        var previous = ReliablePaste.ReadText();
        var before = ReliablePaste.SequenceNumber();
        NativeMethods.SendCtrlC();

        string? copied = null;
        var changed = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(25);
            if (ReliablePaste.SequenceNumber() != before)
            {
                changed = true;
                await Task.Delay(40);   // let the owner finish writing every format
                copied = ReliablePaste.ReadText();
                break;
            }
        }

        // A copy happened (text or not): the user's clipboard was overwritten,
        // so put it back — privately, so it doesn't land in Win+V history.
        if (changed && previous != null) ReliablePaste.RestorePrivate(previous);

        if (!changed || string.IsNullOrWhiteSpace(copied)) return (CopyProbe.NothingSelected, null);
        if (copied.EndsWith('\n')) return (CopyProbe.NothingSelected, null);
        return (CopyProbe.Selection, copied);
    }

    private static bool IsEditLikeControl(string controlType) =>
        controlType.Contains("Edit", StringComparison.OrdinalIgnoreCase)
        || controlType.Contains("Document", StringComparison.OrdinalIgnoreCase);

    private static string ProcessNameOf(IntPtr hwnd)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Process name of the foreground window, for Notes/stats ("Top apps").</summary>
    public static string ForegroundAppName()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    // ----- Clipboard plumbing (STA + lock-retry) -----

    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    private static string? TryGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static void OnUIThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    private static T OnUIThread<T>(Func<T> func)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return func();
        return dispatcher.Invoke(func);
    }
}
