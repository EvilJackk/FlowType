namespace FlowType.Session;

/// <summary>
/// The things a person can ask FlowType to do from the UI or a shortcut,
/// gathered in one place so windows never reach into the session internals.
/// </summary>
public static class UserActions
{
    /// <summary>Insert the most recent dictation again at the caret (Alt+Shift+Z).</summary>
    public static Task PasteLastAsync() => DictationSession.Instance.PasteLastAsync();

    /// <summary>Copy the most recent dictation to the clipboard (Alt+Shift+X).</summary>
    public static void CopyLast() => DictationSession.Instance.CopyLast();

    /// <summary>Whether a note still has its recording on disk.</summary>
    public static bool CanRetranscribe(Note note) => RecordingArchive.Instance.Has(note.Id);

    /// <summary>
    /// Run a kept recording through the current model again and update the
    /// note. Returns the new text, or null when there is no audio or nothing
    /// was recognised.
    /// </summary>
    public static Task<string?> RetranscribeAsync(Note note) => DictationSession.Instance.RetranscribeAsync(note);

    /// <summary>Play a kept recording through the default output device.</summary>
    public static void PlayRecording(Note note) => RecordingArchive.Instance.Play(note.Id);

    /// <summary>Stop any recording that <see cref="PlayRecording"/> started.</summary>
    public static void StopPlayback() => RecordingArchive.Instance.StopPlayback();
}
