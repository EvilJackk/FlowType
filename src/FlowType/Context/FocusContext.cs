using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using FlowType.Core;
using FlowType.Engine;

namespace FlowType.Context;

/// <summary>
/// Everything FlowType knows about where a dictation is going: the window and
/// app, the kind of control, and — when the control could be read — the text
/// around the caret and the names found near it.
///
/// <see cref="ToString"/> is overridden to <see cref="FocusContext.Describe"/>
/// so an accidental <c>$"{snapshot}"</c> in a log line can never write the
/// user's field text or window title to disk.
/// </summary>
/// <param name="Window">Foreground top-level window when the snapshot was taken.</param>
/// <param name="ProcessId">Process that owns <paramref name="Window"/>.</param>
/// <param name="ProcessName">Its executable name without ".exe" (same form as TextInjector.ForegroundAppName).</param>
/// <param name="WindowTitle">The window's caption, capped at 512 characters.</param>
/// <param name="ControlType">UI Automation control type of the focused element ("Edit", "Document"…), or empty.</param>
/// <param name="IsPassword">The focused field is a password field: nothing in it was read.</param>
/// <param name="IsEditable">The focused element accepts typing (not read-only).</param>
/// <param name="Caret">Text before/after the caret and the selection; Known only for an editable field read through TextPattern.</param>
/// <param name="Terms">Names and terms from the field and the window title, best first.</param>
/// <param name="ElapsedMs">How long the capture took, for the diagnostic log.</param>
public sealed record FocusSnapshot(
    IntPtr Window,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string ControlType,
    bool IsPassword,
    bool IsEditable,
    CaretContext Caret,
    IReadOnlyList<string> Terms,
    long ElapsedMs)
{
    /// <summary>Nothing is known: no window, no field, no terms.</summary>
    public static FocusSnapshot Unknown { get; } = new(
        IntPtr.Zero, 0, "", "", "", false, false, CaretContext.Unknown, Array.Empty<string>(), 0);

    /// <summary>
    /// The UI Automation element that had focus (an AutomationElement), kept
    /// so the same field can be read again later (<see cref="FocusContext.ReadCaretAsync"/>,
    /// Session.EditLearner). Typed as object so nothing outside
    /// <see cref="FocusContext"/> depends on UI Automation types, and only ever
    /// touched on FocusContext's worker thread.
    /// </summary>
    internal object? Element { get; init; }

    /// <summary>
    /// The selection in the field was longer than
    /// <see cref="FocusContext.MaxSelectionChars"/>, so <see cref="Caret"/>'s
    /// Selection holds only its first part. Anything that acts on the whole
    /// selection — Command Mode replaces it with the model's answer — must
    /// refuse rather than work from the cut copy: the paste would overwrite
    /// the full selection with a rewrite of its first 4,000 characters.
    /// False when the selection fit, when nothing was read, and for a
    /// snapshot of a field with no selection.
    /// </summary>
    public bool SelectionTruncated { get; init; }

    public override string ToString() => FocusContext.Describe(this);
}

/// <summary>
/// Reads the focused text field through Windows UI Automation, on this PC
/// only, so a dictation can fit where it lands and the recogniser can be told
/// the names that are on screen.
///
/// This is the local half of what Wispr Flow's native helper does on Windows
/// (UIA TextPattern before/after/selection, 500 characters a side, cut at a
/// word boundary). FlowType's version is written from the observed behaviour;
/// the term extraction that Wispr does in the cloud is done here by
/// <see cref="ContextTerms"/>.
///
/// Threading — the reason this class is shaped the way it is:
/// - Every UI Automation call runs on ONE long-lived background MTA thread fed
///   by a queue. UIA calls are cross-process COM calls into the target app; an
///   app that is hung (or busy rendering a game frame) can stall them for many
///   seconds. They must never run on the WPF thread, and a timeout must never
///   be "solved" by starting another thread per call.
/// - Callers get an answer within their timeout no matter what. On timeout the
///   caller gets the fallback; the worker finishes the stale call whenever the
///   app answers and moves on. A job whose deadline passed while it was still
///   queued is never started.
/// - If the worker has been stuck on one call for <see cref="WedgedAfter"/>,
///   it is retired and a fresh one takes over, at most
///   <see cref="MaxRetiredWorkers"/> times concurrently — bounded, so a
///   permanently hung app costs at most a couple of parked threads, never one
///   per dictation.
///
/// Privacy: password fields (UIA IsPassword) are never read; a short list of
/// password managers and Windows security prompts is never read at all; the
/// <see cref="AppSettings.UseScreenContext"/> switch turns every UIA read off.
/// Field text stays in memory and is never logged — <see cref="Describe"/>
/// reports lengths only.
/// </summary>
public static class FocusContext
{
    /// <summary>Characters kept on each side of the caret (Wispr uses the same 500).</summary>
    public const int ContextChars = 500;

    /// <summary>Characters requested on each side before trimming to a word boundary.</summary>
    private const int ReadChars = 600;

    /// <summary>A longer selection is truncated; what matters is its edges.</summary>
    public const int MaxSelectionChars = 4000;

    /// <summary>Term list size handed to the recogniser.</summary>
    public const int MaxTerms = 24;

    /// <summary>Of which at most this many come from the window title.</summary>
    private const int MaxTitleTerms = 8;

    /// <summary>A ValuePattern-only field's text is cut to its tail before term extraction.</summary>
    private const int MaxValueChars = 4000;

    /// <summary>Jobs waiting behind a slow one beyond this are refused, not queued.</summary>
    private const int MaxQueued = 4;

    /// <summary>A single UIA call running this long means the target app is hung.</summary>
    private static readonly TimeSpan WedgedAfter = TimeSpan.FromSeconds(20);

    /// <summary>Hung workers allowed to exist at once before reads are simply refused.</summary>
    private const int MaxRetiredWorkers = 2;

    /// <summary>
    /// Processes whose fields are never read, whatever their control says
    /// about itself: password managers and the Windows credential/elevation
    /// prompts. A vault's search box is not a password field, but what it
    /// shows around the caret can be just as sensitive.
    /// </summary>
    private static readonly HashSet<string> NeverRead = new(StringComparer.OrdinalIgnoreCase)
    {
        "1password", "keepass", "keepassxc", "bitwarden", "lastpass", "dashlane", "enpass",
        "nordpass", "roboform", "keeper", "protonpass", "credentialuibroker", "consent",
        "logonui", "lockapp", "securityhealthsystray",
    };

    // ----- Public API -----

    /// <summary>
    /// Snapshot the foreground window and its focused field.
    ///
    /// Never throws. The window handle, process and title are read directly
    /// (cheap Win32 calls that cannot hang); everything that needs UI
    /// Automation runs on the worker and is simply missing — Caret unknown,
    /// no field terms, no element — if it does not answer within
    /// <paramref name="timeout"/> or <paramref name="ct"/> fires. With
    /// <see cref="AppSettings.UseScreenContext"/> off nothing is read through
    /// UI Automation and no terms are extracted.
    /// </summary>
    public static async Task<FocusSnapshot> CaptureAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        IntPtr hwnd;
        int pid;
        string title;
        try
        {
            hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return FocusSnapshot.Unknown;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var upid);
            pid = (int)upid;
            title = WindowTitle(hwnd);
        }
        catch
        {
            return FocusSnapshot.Unknown;
        }
        var processName = ProcessNameOf(pid);

        if (!ScreenContextAllowed() || NeverRead.Contains(processName))
        {
            // Off, or a password manager: not even the title becomes terms
            // (a vault window's title is often the item being viewed).
            return Build(hwnd, pid, processName, title, null, includeTitleTerms: false, clock);
        }

        var read = await RunAsync(ReadFocused, timeout - clock.Elapsed, ct).ConfigureAwait(false);
        return Build(hwnd, pid, processName, title, read, includeTitleTerms: true, clock);
    }

    /// <summary>
    /// Read the caret context of the field in <paramref name="target"/> again,
    /// as it is now. Unknown when the snapshot holds no element, the field is
    /// a password field, it has gone away, or it does not answer in time.
    /// </summary>
    public static async Task<CaretContext> ReadCaretAsync(FocusSnapshot target, TimeSpan timeout)
    {
        if (target?.Element is not AutomationElement element || target.IsPassword) return CaretContext.Unknown;
        if (!ScreenContextAllowed()) return CaretContext.Unknown;
        var read = await RunAsync(() => ReadElement(element), timeout, CancellationToken.None).ConfigureAwait(false);
        return read?.Caret ?? CaretContext.Unknown;
    }

    /// <summary>
    /// Load and initialise UI Automation on the worker without reading any
    /// app: the first UIA call in a process costs a few hundred milliseconds,
    /// which would otherwise land on the first dictation. Fire-and-forget.
    /// </summary>
    public static void Prewarm()
    {
        if (!ScreenContextAllowed()) return;
        _ = RunAsync<object>(() =>
        {
            // The desktop root is FlowType's own question to the system; no
            // app's content is touched.
            _ = AutomationElement.RootElement.Current.ControlType;
            return new object();
        }, TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    /// <summary>
    /// One line about a snapshot for the diagnostic log: app, control kind,
    /// flags and the LENGTHS of what was read. Never any field text, title
    /// text or term.
    /// </summary>
    internal static string Describe(FocusSnapshot? s)
    {
        if (s is null) return "focus: none";
        try
        {
            var app = new string((s.ProcessName ?? "").Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+')
                .Take(40).ToArray());
            var caret = s.Caret ?? CaretContext.Unknown;
            return $"focus: app={(app.Length == 0 ? "?" : app)} pid={s.ProcessId} " +
                   $"hwnd=0x{s.Window.ToInt64():X} control={(string.IsNullOrEmpty(s.ControlType) ? "?" : s.ControlType)} " +
                   $"editable={(s.IsEditable ? "yes" : "no")} password={(s.IsPassword ? "yes" : "no")} " +
                   $"caret={(caret.Known ? "known" : "unknown")} before={caret.Before.Length} " +
                   $"selection={caret.Selection.Length}{(s.SelectionTruncated ? "+" : "")} after={caret.After.Length} " +
                   $"title={s.WindowTitle?.Length ?? 0}ch terms={s.Terms?.Count ?? 0} " +
                   $"element={(s.Element != null ? "held" : "none")} {s.ElapsedMs}ms";
        }
        catch
        {
            return "focus: (unavailable)";
        }
    }

    // ----- Re-reading a field (EditLearner) -----

    internal enum WindowReadStatus
    {
        /// <summary>No answer (timeout, busy app, transient UIA error). Worth trying again.</summary>
        Failed,
        /// <summary>The element no longer exists or is now a password field. Stop.</summary>
        Gone,
        Ok,
    }

    /// <summary>
    /// Raw text around the current caret of a field: <paramref name="Text"/> is
    /// before + selection + after with nothing trimmed, and the flags say
    /// whether the read reached the start/end of the field.
    /// </summary>
    internal sealed record TextWindow(WindowReadStatus Status, string Text, bool AtStart, bool AtEnd)
    {
        internal static TextWindow Failed { get; } = new(WindowReadStatus.Failed, "", false, false);
        internal static TextWindow Gone { get; } = new(WindowReadStatus.Gone, "", false, false);
    }

    /// <summary>
    /// Read up to <paramref name="span"/> characters on each side of the
    /// field's current selection. Used to find a dictation again after the
    /// user may have edited it. When the field reports no selection (it lost
    /// focus, as Chromium fields do), the read starts at the top of the field
    /// instead — which covers the chat boxes and forms most dictations go into.
    /// </summary>
    internal static async Task<TextWindow> ReadWindowAsync(FocusSnapshot target, int span, TimeSpan timeout)
    {
        if (target?.Element is not AutomationElement element || target.IsPassword) return TextWindow.Gone;
        if (!ScreenContextAllowed()) return TextWindow.Gone;
        span = Math.Clamp(span, 100, 20_000);
        var result = await RunAsync(() => ReadWindow(element, span), timeout, CancellationToken.None)
            .ConfigureAwait(false);
        return result ?? TextWindow.Failed;
    }

    /// <summary>
    /// Diagnostics / --selftest: snapshot a specific control by window handle
    /// instead of whatever has focus, so FlowType can test itself on its own
    /// (hidden) controls without moving focus or sending input. Ignores the
    /// UseScreenContext switch — it only ever reads the handle it is given.
    /// </summary>
    internal static async Task<FocusSnapshot> CaptureWindowAsync(IntPtr control, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        if (control == IntPtr.Zero) return FocusSnapshot.Unknown;
        int pid;
        string title;
        IntPtr root;
        try
        {
            // The snapshot describes the top-level window, as CaptureAsync's
            // does; a child control's own "title" is its text.
            root = GetAncestor(control, GaRoot);
            if (root == IntPtr.Zero) root = control;
            NativeMethods.GetWindowThreadProcessId(root, out var upid);
            pid = (int)upid;
            title = WindowTitle(root);
        }
        catch
        {
            return FocusSnapshot.Unknown;
        }
        var read = await RunAsync<ElementRead>(() =>
        {
            AutomationElement? element;
            try { element = AutomationElement.FromHandle(control); }
            catch { return null; }
            return element == null ? null : ReadElement(element);
        }, timeout - clock.Elapsed, CancellationToken.None).ConfigureAwait(false);
        return Build(root, pid, ProcessNameOf(pid), title, read, includeTitleTerms: true, clock);
    }

    // ----- Snapshot assembly (caller's thread, no UIA) -----

    /// <summary>What the worker found in one element. Lives only between the worker and Build.</summary>
    private sealed record ElementRead(
        AutomationElement Element, string ControlType, bool IsPassword, bool IsEditable,
        CaretContext Caret, string TermText, bool SelectionTruncated = false);

    private static FocusSnapshot Build(IntPtr hwnd, int pid, string processName, string title,
        ElementRead? read, bool includeTitleTerms, Stopwatch clock)
    {
        IReadOnlyList<string> terms = Array.Empty<string>();
        try
        {
            // Term extraction is pure CPU and runs here, off the worker, so a
            // slow regex-free pass over 5 K of text never delays the next UIA job.
            var titleTerms = includeTitleTerms
                ? ContextTerms.ExtractFromTitle(title, processName, MaxTitleTerms)
                : Array.Empty<string>();
            var fieldTerms = read is { IsPassword: false } && read.TermText.Length > 0
                ? ContextTerms.Extract(read.TermText, MaxTerms)
                : Array.Empty<string>();
            terms = ContextTerms.Merge(MaxTerms, titleTerms, fieldTerms);
        }
        catch
        {
            // A term list is a nicety; a bug in it must never cost the dictation.
        }

        return new FocusSnapshot(
            hwnd, pid, processName, title,
            read?.ControlType ?? "",
            read?.IsPassword ?? false,
            read?.IsEditable ?? false,
            read?.Caret ?? CaretContext.Unknown,
            terms,
            clock.ElapsedMilliseconds)
        {
            Element = read?.Element,
            SelectionTruncated = read?.SelectionTruncated ?? false,
        };
    }

    private static bool ScreenContextAllowed()
    {
        try { return SettingsStore.Instance.Settings.UseScreenContext; }
        catch { return false; }
    }

    // ----- UI Automation (worker thread only) -----

    private static ElementRead? ReadFocused()
    {
        AutomationElement? element;
        try { element = AutomationElement.FocusedElement; }
        catch { return null; }
        return element == null ? null : ReadElement(element);
    }

    /// <summary>
    /// Read one element: what it is, whether it is a password field, whether
    /// it takes typing, and the caret context. Properties are fetched in one
    /// cached round trip where the provider allows it — every separate
    /// property read is another cross-process call into the target app.
    /// </summary>
    private static ElementRead? ReadElement(AutomationElement element)
    {
        AutomationElement source = element;
        var cached = false;
        try
        {
            var request = new CacheRequest
            {
                AutomationElementMode = AutomationElementMode.Full,
                TreeScope = TreeScope.Element,
            };
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.IsPasswordProperty);
            request.Add(AutomationElement.IsTextPatternAvailableProperty);
            request.Add(AutomationElement.IsValuePatternAvailableProperty);
            request.Add(ValuePattern.IsReadOnlyProperty);
            request.Add(TextPattern.Pattern);
            request.Add(ValuePattern.Pattern);
            source = element.GetUpdatedCache(request);
            cached = true;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch
        {
            // Some providers reject cache requests; fall back to live reads.
        }

        string controlType;
        bool isPassword;
        try
        {
            var info = cached ? source.Cached : source.Current;
            controlType = ControlTypeName(info.ControlType);
            isPassword = info.IsPassword;
        }
        catch
        {
            return null;
        }

        if (isPassword)
        {
            // Never read a password field — not even its length.
            return new ElementRead(element, controlType, true, true, CaretContext.Unknown, "");
        }

        TextPattern? text = null;
        ValuePattern? value = null;
        try
        {
            if (TryPattern(source, cached, TextPattern.Pattern, out var t)) text = t as TextPattern;
            if (TryPattern(source, cached, ValuePattern.Pattern, out var v)) value = v as ValuePattern;
        }
        catch
        {
            // Pattern discovery failed: treat as an unreadable control.
        }

        bool? valueReadOnly = null;
        if (value != null)
        {
            try { valueReadOnly = cached ? value.Cached.IsReadOnly : value.Current.IsReadOnly; }
            catch { valueReadOnly = null; }
        }

        if (text != null)
        {
            try
            {
                var around = ReadAround(text, ReadChars, out var selectionRange);
                if (around != null)
                {
                    bool? textReadOnly = null;
                    try
                    {
                        if (selectionRange?.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is bool ro) textReadOnly = ro;
                    }
                    catch
                    {
                        textReadOnly = null;
                    }

                    var editable = valueReadOnly == false
                                   || textReadOnly == false
                                   || (textReadOnly == null && valueReadOnly == null && controlType is "Edit" or "Document");

                    var before = TrimBefore(around.Before, ContextChars, around.AtStart);
                    var after = TrimAfter(around.After, ContextChars, around.AtEnd);
                    // A caret in text that cannot be typed into says nothing
                    // about where the dictation will land.
                    var caret = editable
                        ? new CaretContext(before, after, around.Selection, true)
                        : CaretContext.Unknown;
                    return new ElementRead(element, controlType, false, editable, caret,
                        before + around.Selection + after, around.SelectionTruncated);
                }
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
            catch
            {
                // Fall through to ValuePattern.
            }
        }

        if (value != null)
        {
            // ValuePattern says what is in the field but not where the caret
            // is, so the text feeds the term list only.
            var editable = valueReadOnly == false;
            var tail = "";
            try
            {
                var all = value.Current.Value ?? "";
                tail = all.Length > MaxValueChars ? all[^MaxValueChars..] : all;
            }
            catch
            {
                tail = "";
            }
            return new ElementRead(element, controlType, false, editable, CaretContext.Unknown, tail);
        }

        return new ElementRead(element, controlType, false, false, CaretContext.Unknown, "");
    }

    private static bool TryPattern(AutomationElement element, bool cached, AutomationPattern pattern, out object? result)
    {
        result = null;
        if (cached)
        {
            if (element.TryGetCachedPattern(pattern, out var p)) { result = p; return true; }
            return false;
        }
        if (element.TryGetCurrentPattern(pattern, out var q)) { result = q; return true; }
        return false;
    }

    private static string ControlTypeName(ControlType? type)
    {
        var name = type?.ProgrammaticName ?? "";
        const string prefix = "ControlType.";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
    }

    private sealed record Around(string Before, string Selection, string After, bool AtStart, bool AtEnd,
        bool SelectionTruncated = false);

    /// <summary>
    /// Text on both sides of the selection, read as two small ranges grown
    /// from the selection's ends. Never DocumentRange.GetText(-1): in Word or
    /// a long Google Doc that is the whole document, pulled across processes.
    /// </summary>
    private static Around? ReadAround(TextPattern text, int span, out TextPatternRange? selectionRange)
    {
        selectionRange = null;
        TextPatternRange[] selections;
        try { selections = text.GetSelection(); }
        catch (InvalidOperationException) { return null; }   // provider supports no selection
        if (selections == null || selections.Length == 0) return null;

        var selection = selections[0];
        selectionRange = selection;

        // One character over the cap is asked for so a selection longer than
        // the cap can be told from one exactly at it; a provider that ignores
        // the limit and returns everything is caught by the same test.
        var selected = selection.GetText(MaxSelectionChars + 1) ?? "";
        var truncated = selected.Length > MaxSelectionChars;
        if (truncated) selected = selected[..MaxSelectionChars];

        var before = selection.Clone();
        before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        var movedBack = before.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -span);
        // Providers disagree on what a "character" is (CRLF as one or two),
        // so allow twice the span back and trim by length afterwards.
        var beforeText = before.GetText(span * 2) ?? "";

        var after = selection.Clone();
        after.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
        var movedForward = after.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, span);
        var afterText = after.GetText(span * 2) ?? "";

        return new Around(beforeText, selected, afterText,
            AtStart: Math.Abs(movedBack) < span,
            AtEnd: movedForward < span,
            SelectionTruncated: truncated);
    }

    /// <summary>
    /// For a field that reports no selection: read from its start. Bounded the
    /// same way as <see cref="ReadAround"/> — a degenerate range at the top of
    /// the document grown by a fixed number of characters.
    /// </summary>
    private static Around ReadFromStart(TextPattern text, int span)
    {
        var doc = text.DocumentRange;
        var range = doc.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.Start);
        var moved = range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, span * 2);
        var content = range.GetText(span * 4) ?? "";
        return new Around("", "", content, AtStart: true, AtEnd: moved < span * 2);
    }

    private static TextWindow ReadWindow(AutomationElement element, int span)
    {
        try
        {
            if (element.Current.IsPassword) return TextWindow.Gone;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var p) || p is not TextPattern text)
            {
                return TextWindow.Gone;
            }
            var around = ReadAround(text, span, out _) ?? ReadFromStart(text, span);
            return new TextWindow(WindowReadStatus.Ok, around.Before + around.Selection + around.After,
                around.AtStart, around.AtEnd);
        }
        catch (ElementNotAvailableException)
        {
            return TextWindow.Gone;
        }
        catch
        {
            return TextWindow.Failed;
        }
    }

    // ----- Trimming to a word boundary -----

    /// <summary>
    /// The last <paramref name="max"/> characters of the text before the
    /// caret, starting at a word boundary so the first word is never a
    /// fragment. Untouched when the read reached the start of the field and
    /// already fits.
    /// </summary>
    internal static string TrimBefore(string text, int max, bool atStart)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cut = !atStart;
        if (text.Length > max)
        {
            text = text[^max..];
            cut = true;
        }
        if (!cut) return text;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) return text[(i + 1)..];
        }
        return text;   // one long token (a URL): keep it rather than nothing
    }

    /// <summary>The first <paramref name="max"/> characters after the caret, ending at a word boundary.</summary>
    internal static string TrimAfter(string text, int max, bool atEnd)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cut = !atEnd;
        if (text.Length > max)
        {
            text = text[..max];
            cut = true;
        }
        if (!cut) return text;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i])) return text[..i];
        }
        return text;
    }

    // ----- Win32 (caller's thread; none of these can block on another app) -----

    /// <summary>
    /// The caption as the window manager stores it. InternalGetWindowText
    /// never sends the window a message. GetWindowText does not either for
    /// another process's window, but for a window of THIS process it sends
    /// WM_GETTEXT and waits — measured in the private harness: a capture of a
    /// FlowType window whose thread was busy blocked for the full 2.4 s of the
    /// stall, before the UIA timeout could even start. FlowType's own windows
    /// are a normal dictation target (the practice box), so the title read
    /// must not depend on any thread being free.
    /// </summary>
    private static string WindowTitle(IntPtr hwnd)
    {
        try
        {
            var buffer = new StringBuilder(512);
            var copied = InternalGetWindowText(hwnd, buffer, buffer.Capacity);
            return copied <= 0 ? "" : buffer.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Executable name without extension. QueryFullProcessImageName needs only
    /// limited-query rights (granted for most elevated processes too) and,
    /// unlike Process.ProcessName, does not snapshot every process on the
    /// machine.
    /// </summary>
    private static string ProcessNameOf(int pid)
    {
        if (pid <= 0) return "";
        try
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    var buffer = new StringBuilder(1024);
                    var size = (uint)buffer.Capacity;
                    if (QueryFullProcessImageNameW(handle, 0, buffer, ref size))
                    {
                        return Path.GetFileNameWithoutExtension(buffer.ToString());
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }
        catch
        {
            // Fall through to the slower managed lookup.
        }
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GaRoot = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    /// <summary>
    /// Exported by user32 since NT 4 and documented as "not intended for
    /// general use" only because GetWindowText is the portable choice; it is
    /// exactly the no-message read wanted here.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int InternalGetWindowText(IntPtr hWnd, StringBuilder pString, int cchMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // ----- The worker -----

    private static readonly object WorkerGate = new();
    private static Worker? _worker;
    private static int _retiredAlive;
    private static int _workersStarted;

    /// <summary>
    /// Diagnostics: worker threads started so far, and retired (hung) ones
    /// still alive. Started &gt; 1 means some app has hung a UIA call for
    /// longer than <see cref="WedgedAfter"/> at least once.
    /// </summary>
    internal static (int Started, int RetiredAlive) WorkerStats
    {
        get { lock (WorkerGate) return (_workersStarted, _retiredAlive); }
    }

    /// <summary>
    /// Run <paramref name="work"/> on the UIA worker and wait at most
    /// <paramref name="timeout"/>. Returns null on timeout, cancellation, a
    /// full queue, a hung worker with no replacement allowed, or any exception.
    /// </summary>
    private static async Task<T?> RunAsync<T>(Func<T?> work, TimeSpan timeout, CancellationToken ct) where T : class
    {
        if (timeout <= TimeSpan.Zero || ct.IsCancellationRequested) return null;
        var job = new Job<T>(work, Environment.TickCount64 + (long)timeout.TotalMilliseconds, ct);
        if (!Post(job)) return null;
        try
        {
            return await job.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            job.Abandon();
            return null;
        }
    }

    private static bool Post(IJob job)
    {
        lock (WorkerGate)
        {
            if (_worker == null)
            {
                _worker = new Worker(OnWorkerExit);
                _workersStarted++;
            }
            if (_worker.IsWedged(WedgedAfter))
            {
                if (_retiredAlive >= MaxRetiredWorkers) return false;
                _worker.Retire();
                _retiredAlive++;
                _worker = new Worker(OnWorkerExit);
                _workersStarted++;
            }
            if (_worker.Pending >= MaxQueued) return false;
            return _worker.TryPost(job);
        }
    }

    private static void OnWorkerExit(Worker worker)
    {
        lock (WorkerGate)
        {
            // Only retired workers ever exit; the live one runs for the life
            // of the process.
            if (!ReferenceEquals(worker, _worker) && _retiredAlive > 0) _retiredAlive--;
        }
    }

    private interface IJob
    {
        bool Expired { get; }
        void Execute();
        void Abandon();
    }

    private sealed class Job<T> : IJob where T : class
    {
        private readonly Func<T?> _work;
        private readonly long _deadline;
        private readonly CancellationToken _ct;
        private readonly TaskCompletionSource<T?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Job(Func<T?> work, long deadline, CancellationToken ct)
        {
            _work = work;
            _deadline = deadline;
            _ct = ct;
        }

        public Task<T?> Task => _done.Task;

        public bool Expired => _ct.IsCancellationRequested || Environment.TickCount64 > _deadline || _done.Task.IsCompleted;

        public void Execute()
        {
            try { _done.TrySetResult(_work()); }
            catch { _done.TrySetResult(null); }
        }

        public void Abandon() => _done.TrySetResult(null);
    }

    /// <summary>One background MTA thread draining a queue of UIA jobs.</summary>
    private sealed class Worker
    {
        private readonly BlockingCollection<IJob> _jobs = new(new ConcurrentQueue<IJob>());
        private long _busySince;

        public Worker(Action<Worker> onExit)
        {
            var thread = new Thread(() =>
            {
                Loop();
                onExit(this);
            })
            {
                IsBackground = true,
                Name = "FlowType UI Automation",
            };
            // UIA client objects are free-threaded under MTA; an STA here
            // would need a message pump to receive cross-apartment calls.
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public int Pending => _jobs.Count;

        public bool IsWedged(TimeSpan after)
        {
            var since = Interlocked.Read(ref _busySince);
            return since != 0 && Environment.TickCount64 - since > (long)after.TotalMilliseconds;
        }

        public bool TryPost(IJob job)
        {
            try { return _jobs.TryAdd(job); }
            catch (InvalidOperationException) { return false; }
        }

        /// <summary>Stop accepting work; everything still queued is answered with its fallback now.</summary>
        public void Retire()
        {
            try
            {
                _jobs.CompleteAdding();
                while (_jobs.TryTake(out var job)) job.Abandon();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        }

        private void Loop()
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                if (job.Expired)
                {
                    job.Abandon();
                    continue;
                }
                Interlocked.Exchange(ref _busySince, Environment.TickCount64);
                try
                {
                    job.Execute();
                }
                catch
                {
                    job.Abandon();
                }
                finally
                {
                    Interlocked.Exchange(ref _busySince, 0);
                }
            }
        }
    }
}
