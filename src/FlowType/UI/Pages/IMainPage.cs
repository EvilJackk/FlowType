using System.Windows;

namespace FlowType.UI.Pages;

/// <summary>
/// What <see cref="MainWindow"/> needs from each page it hosts.
///
/// Pages are created the first time they are opened and kept for the life
/// of the window, but only the visible one sits in the visual tree. Each
/// page subscribes to the services it shows in its constructor and must undo
/// that in <see cref="Detach"/> — the services are process-wide singletons
/// and would otherwise keep a closed window alive and keep repainting it.
/// </summary>
internal interface IMainPage
{
    /// <summary>The page just became the visible one: refresh anything that may be stale.</summary>
    void OnShown();

    /// <summary>The page is being swapped out: stop meters, timers, captures.</summary>
    void OnHidden();

    /// <summary>The window is closing: unsubscribe from every service event.</summary>
    void Detach();

    /// <summary>The page's whole content column, rendered at full height by --snapshot.</summary>
    FrameworkElement SnapshotBody { get; }
}
