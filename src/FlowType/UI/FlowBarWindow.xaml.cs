using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlowType.Audio;
using FlowType.Core;
using FlowType.Input;
using FlowType.Session;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;

namespace FlowType.UI;

/// <summary>
/// The flow bar — a small always-on pill pinned bottom-center (Wispr Flow's
/// signature UI). Idle it's a quiet mini-waveform; while listening it expands
/// into a live waveform with a timer; then a brief "✓ n words" flash.
/// Click toggles hands-free dictation. Never steals focus (WS_EX_NOACTIVATE).
/// </summary>
public partial class FlowBarWindow : Window
{
    private const int LiveBarCount = 16;
    private const int IdleBarCount = 5;
    private const double LiveBarMin = 2, LiveBarMax = 16;

    // Pill geometry per state. Kept deliberately low (≤ 28 px) so the bar
    // never covers the message box at the bottom of chat apps.
    private const double IdleWidth = 72, IdleHeight = 22;
    private const double RecordingWidth = 232, RecordingHeight = 28;
    private const double ProcessingWidth = 150, ProcessingHeight = 26;
    private const double MessageHeight = 26, MaxMessageWidth = 440;

    private readonly Rectangle[] _liveBars = new Rectangle[LiveBarCount];
    private readonly double[] _levels = new double[LiveBarCount];
    private readonly DispatcherTimer _animTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(50),
    };
    private readonly DispatcherTimer _flashTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(1700),
    };
    private bool _allowClose;
    private bool _flashActive;
    private IntPtr _barHandle = IntPtr.Zero;
    private IntPtr _previousForeground = IntPtr.Zero;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public FlowBarWindow()
    {
        InitializeComponent();

        var liveBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4));
        for (var i = 0; i < LiveBarCount; i++)
        {
            var bar = new Rectangle
            {
                Width = 2.5,
                Height = LiveBarMin,
                RadiusX = 1.25,
                RadiusY = 1.25,
                Margin = new Thickness(1.4, 0, 1.4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = liveBrush,
            };
            _liveBars[i] = bar;
            BarsPanel.Children.Add(bar);
        }

        var idleBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        var idleHeights = new[] { 4.0, 7.0, 10.0, 7.0, 4.0 };
        for (var i = 0; i < IdleBarCount; i++)
        {
            IdlePanel.Children.Add(new Rectangle
            {
                Width = 2.5,
                Height = idleHeights[i],
                RadiusX = 1.25,
                RadiusY = 1.25,
                Margin = new Thickness(1.8, 0, 1.8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = idleBrush,
                Opacity = 0.8,
            });
        }

        _animTimer.Tick += (_, _) => OnAnimTick();
        _flashTimer.Tick += (_, _) =>
        {
            _flashTimer.Stop();
            _flashActive = false;
            ApplyState(DictationSession.Instance.State, "");
        };

        DictationSession.Instance.StateChanged += (state, detail) =>
            Dispatcher.BeginInvoke(() => ApplyState(state, detail));
        DictationSession.Instance.Flash += (message, isError) =>
            Dispatcher.BeginInvoke(() => ShowFlash(message, isError));
        AudioRecorder.Instance.LevelChanged += level =>
            Dispatcher.BeginInvoke(() => PushLevel(level));
        SettingsStore.Instance.Changed += OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        StartPulse(RecDot, 0.65);
        StartDotWave();
        UpdateTooltip();
        ApplyState(SessionState.Idle, "");
    }

    // ----- State rendering -----

    private void ApplyState(SessionState state, string detail)
    {
        if (_flashActive && state == SessionState.Idle) return;

        IdlePanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Collapsed;
        MessageText.Visibility = Visibility.Collapsed;

        switch (state)
        {
            case SessionState.Recording:
                Array.Clear(_levels);
                TimeText.Text = "0:00";
                RecordingPanel.Visibility = Visibility.Visible;
                _animTimer.Start();
                MorphBar(RecordingWidth, RecordingHeight);
                ShowBar();
                break;

            case SessionState.Processing:
                _animTimer.Stop();
                ProcessingPanel.Visibility = Visibility.Visible;
                MorphBar(ProcessingWidth, ProcessingHeight);
                ShowBar();
                break;

            case SessionState.Error:
                _animTimer.Stop();
                MessageText.Text = detail;
                MessageText.Foreground = (Brush)FindResource("DangerBrush");
                MessageText.Visibility = Visibility.Visible;
                MorphBar(Math.Min(MaxMessageWidth, 60 + detail.Length * 6.2), MessageHeight);
                ShowBar();
                break;

            case SessionState.Idle:
            default:
                _animTimer.Stop();
                if (SettingsStore.Instance.Settings.ShowBarWhenIdle
                    && SettingsStore.Instance.Settings.BarPosition != "hidden")
                {
                    IdlePanel.Visibility = Visibility.Visible;
                    MorphBar(IdleWidth, IdleHeight);
                    ShowBar();
                }
                else
                {
                    Hide();
                }
                break;
        }
    }

    private void ShowFlash(string message, bool isError)
    {
        // Errors set via StateChanged stay; flashes are transient toasts.
        _flashActive = true;
        IdlePanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = Visibility.Collapsed;
        ProcessingPanel.Visibility = Visibility.Collapsed;

        MessageText.Text = message;
        MessageText.Foreground = isError
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextBrush");
        MessageText.Visibility = Visibility.Visible;
        MorphBar(Math.Min(MaxMessageWidth, 44 + message.Length * 6.4), MessageHeight);
        ShowBar();

        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void MorphBar(double width, double height)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Bar.BeginAnimation(WidthProperty,
            new DoubleAnimation(width, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        Bar.BeginAnimation(HeightProperty,
            new DoubleAnimation(height, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    private void ShowBar()
    {
        // "Hidden" keeps FlowType fully usable from the tray and hotkey while
        // never drawing on screen (handy for screen shares and full-screen games).
        if (SettingsStore.Instance.Settings.BarPosition == "hidden")
        {
            Hide();
            return;
        }
        Reposition();
        if (!IsVisible) Show();
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = SettingsStore.Instance.Settings.BarPosition == "top"
            ? area.Top
            : area.Bottom - Height;
    }

    // ----- Waveform -----

    private void PushLevel(float level)
    {
        for (var i = 0; i < LiveBarCount - 1; i++) _levels[i] = _levels[i + 1];
        _levels[^1] = Math.Max(level, _levels[^2] * 0.6);
    }

    private void OnAnimTick()
    {
        var elapsed = DateTime.Now - DictationSession.Instance.RecordingStartTime;
        TimeText.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

        // Real mic level rides on a gentle rolling swell so the bar always
        // visibly "listens", even in silence.
        var t = elapsed.TotalSeconds;
        for (var i = 0; i < LiveBarCount; i++)
        {
            var idle = 0.05 + 0.06 * (0.5 + 0.5 * Math.Sin(t * 5.2 + i * 0.8));
            var normalized = Math.Min(1.0, Math.Max(_levels[i] * 1.8, idle));
            _liveBars[i].Height = LiveBarMin + normalized * (LiveBarMax - LiveBarMin);
        }
    }

    private static void StartPulse(UIElement element, double lowOpacity)
    {
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1.0, lowOpacity * 0.5, TimeSpan.FromSeconds(0.65))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
    }

    private void StartDotWave()
    {
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].BeginAnimation(OpacityProperty,
                new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(0.5))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromMilliseconds(i * 160),
                });
        }
    }

    // ----- Interaction -----

    private void Bar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // A left click while the menu is up means "dismiss", not "dictate".
        if (BarMenu.IsOpen)
        {
            BarMenu.IsOpen = false;
            return;
        }
        DictationSession.Instance.Toggle();
    }

    /// <summary>
    /// The flow bar is a WS_EX_NOACTIVATE window so it never steals focus from
    /// whatever you're dictating into. That style also breaks its context menu:
    /// a menu whose owner can't activate never gains focus, so it can't notice
    /// you clicking elsewhere and stays stuck open until you pick something.
    ///
    /// Fix: drop WS_EX_NOACTIVATE for exactly as long as the menu is up and let
    /// the bar activate normally, so the menu behaves like any other menu
    /// (click-away and Esc both dismiss it). We remember which window had focus
    /// beforehand and hand it straight back on close, so the next dictation
    /// still lands in the app you were using.
    /// </summary>
    private void Bar_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        MenuDictate.Header = DictationSession.Instance.State == SessionState.Recording
            ? "Stop dictating"
            : "Start dictating";

        _previousForeground = NativeMethods.GetForegroundWindow();
        SetNoActivate(false);
        if (_barHandle != IntPtr.Zero) NativeMethods.SetForegroundWindow(_barHandle);
    }

    private void BarMenu_Closed(object sender, RoutedEventArgs e)
    {
        SetNoActivate(true);
        if (_previousForeground != IntPtr.Zero && _previousForeground != _barHandle)
        {
            NativeMethods.SetForegroundWindow(_previousForeground);
        }
        _previousForeground = IntPtr.Zero;
    }

    private void SetNoActivate(bool enabled)
    {
        if (_barHandle == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE);
        style = enabled
            ? style | NativeMethods.WS_EX_NOACTIVATE
            : style & ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE, style);
    }

    private void MenuDictate_Click(object sender, RoutedEventArgs e) =>
        DictationSession.Instance.Toggle();

    private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void MenuSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void MenuQuit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void MenuHideBar_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Instance.Settings.BarPosition = "hidden";
        SettingsStore.Instance.Save();
        Hide();
    }

    private void UpdateTooltip()
    {
        var hotkey = HotkeyDefs.CurrentDisplay(SettingsStore.Instance.Settings);
        Bar.ToolTip = $"Hold {hotkey} to talk • tap it for hands-free • Esc cancels • click to toggle";
    }

    // ----- Snapshot (hidden diagnostic) -----

    /// <summary>Render each bar state to PNG over a neutral backdrop.</summary>
    internal void SnapshotStates(string dir)
    {
        var root = (FrameworkElement)Content;
        var backdrop = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));

        void Shoot(string name, double width, double height)
        {
            // Animations would otherwise hold the old size for 180 ms.
            Bar.BeginAnimation(WidthProperty, null);
            Bar.BeginAnimation(HeightProperty, null);
            Bar.Width = width;
            Bar.Height = height;
            UpdateLayout();
            Snapshot.Save(root, System.IO.Path.Combine(dir, $"bar-{name}.png"), backdrop);
        }

        _flashActive = false;
        ApplyState(SessionState.Idle, "");
        Shoot("idle", IdleWidth, IdleHeight);

        ApplyState(SessionState.Recording, "");
        _animTimer.Stop();
        var demo = new[] { 0.2, 0.5, 0.9, 0.6, 0.3, 0.7, 1.0, 0.5, 0.25, 0.6, 0.8, 0.4, 0.2, 0.5, 0.3, 0.15 };
        for (var i = 0; i < LiveBarCount; i++)
        {
            _liveBars[i].Height = LiveBarMin + demo[i] * (LiveBarMax - LiveBarMin);
        }
        TimeText.Text = "0:07";
        Shoot("recording", RecordingWidth, RecordingHeight);

        ApplyState(SessionState.Processing, "");
        Shoot("processing", ProcessingWidth, ProcessingHeight);

        const string flash = "✓ 42 words";
        ShowFlash(flash, false);
        _flashTimer.Stop();
        Shoot("flash", Math.Min(MaxMessageWidth, 44 + flash.Length * 6.4), MessageHeight);

        _flashActive = false;
        const string error = "Microphone unavailable: no input device";
        ApplyState(SessionState.Error, error);
        Shoot("error", Math.Min(MaxMessageWidth, 60 + error.Length * 6.2), MessageHeight);

        _flashActive = false;
        ApplyState(SessionState.Idle, "");
    }

    // ----- Window plumbing -----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _barHandle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(() =>
    {
        UpdateTooltip();
        if (DictationSession.Instance.State == SessionState.Idle && !_flashActive)
        {
            ApplyState(SessionState.Idle, "");
        }
    });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(Reposition);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SettingsStore.Instance.Changed -= OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosing(e);
    }

    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }
}
