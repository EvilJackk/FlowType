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
    public static async Task PasteAsync(string text, bool restoreClipboard)
    {
        // Preferred path: publish a clipboard promise and restore only once the
        // target has actually read it (see ReliablePaste). Nothing to "settle"
        // before the chord either, which takes ~120 ms out of every dictation.
        if (await ReliablePaste.PasteAsync(text, restoreClipboard)) return;

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
