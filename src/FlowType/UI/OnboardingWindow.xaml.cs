using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlowType.Audio;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;
using TextWrapping = System.Windows.TextWrapping;

namespace FlowType.UI;

/// <summary>
/// First-run wizard: welcome → microphone (live meter) → model download →
/// hotkey + live practice. The dictation session is already armed while this
/// window is open, so step 4 is a real test, not a simulation.
/// </summary>
public partial class OnboardingWindow : Window
{
    private int _step = 1;
    private readonly Dictionary<string, ProgressBar> _bars = new();
    private bool _capturing;
    private bool _loadingControls = true;

    public event Action? Completed;

    public OnboardingWindow()
    {
        InitializeComponent();

        LoadMics();
        LoadHotkeys();
        LoadVariants();
        BuildModelList();
        UpdateChrome();
        _loadingControls = false;

        ModelDownloader.Instance.ProgressChanged += OnProgress;
        ModelDownloader.Instance.Completed += OnDownloadDone;
        Transcriber.Instance.StateChanged += OnTranscriberChanged;
        AudioRecorder.Instance.LevelChanged += OnLevel;
    }

    protected override void OnClosed(EventArgs e)
    {
        AudioRecorder.Instance.StopMonitor();
        if (_capturing) App.Hotkeys?.CancelCapture();
        ModelDownloader.Instance.ProgressChanged -= OnProgress;
        ModelDownloader.Instance.Completed -= OnDownloadDone;
        Transcriber.Instance.StateChanged -= OnTranscriberChanged;
        AudioRecorder.Instance.LevelChanged -= OnLevel;
        base.OnClosed(e);
    }

    // ----- Steps -----

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 3 && !AnyModelReady())
        {
            MessageBox.Show(this, "Download a model first — Base is a quick 142 MB.",
                "FlowType", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_step == 4)
        {
            SettingsStore.Instance.Settings.OnboardingComplete = true;
            SettingsStore.Instance.Save();
            Completed?.Invoke();
            Close();
            return;
        }
        _step++;
        UpdateChrome();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1) _step--;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Content = _step == 4 ? "Finish" : _step == 1 ? "Get started" : "Next";

        var accent = (Brush)FindResource("AccentBrush");
        var faint = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        Step1Dot.Fill = _step >= 1 ? accent : faint;
        Step2Dot.Fill = _step >= 2 ? accent : faint;
        Step3Dot.Fill = _step >= 3 ? accent : faint;
        Step4Dot.Fill = _step >= 4 ? accent : faint;

        // Live mic meter only on the mic step.
        if (_step == 2)
        {
            try { AudioRecorder.Instance.StartMonitor(); } catch { }
        }
        else
        {
            AudioRecorder.Instance.StopMonitor();
        }

        if (_step == 4)
        {
            UpdatePracticeHint();
            PracticeBox.Focus();
        }
    }

    // ----- Mic -----

    private void LoadMics()
    {
        var saved = SettingsStore.Instance.Settings.MicDeviceName;
        MicCombo.Items.Clear();
        MicCombo.Items.Add(new ComboBoxItem { Content = "System default", Tag = "" });
        foreach (var name in AudioRecorder.GetInputDeviceNames())
        {
            MicCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }
        var index = 0;
        for (var i = 1; i < MicCombo.Items.Count; i++)
        {
            if (saved.Length > 0 && (string)((ComboBoxItem)MicCombo.Items[i]).Tag == saved)
            {
                index = i;
                break;
            }
        }
        MicCombo.SelectedIndex = index;
    }

    private void RefreshMics_Click(object sender, RoutedEventArgs e) => LoadMics();

    private void Mic_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (MicCombo.SelectedItem is ComboBoxItem item && item.Tag is string device)
        {
            SettingsStore.Instance.Settings.MicDeviceName = device;
            SettingsStore.Instance.Save();
            // Restart the meter on the new device.
            AudioRecorder.Instance.StopMonitor();
            if (_step == 2)
            {
                try { AudioRecorder.Instance.StartMonitor(); } catch { }
            }
        }
    }

    private void OnLevel(float level) => Dispatcher.BeginInvoke(() =>
    {
        if (_step == 2) LevelBar.Value = Math.Min(1.0, level * 1.6);
    });

    // ----- Model -----

    private static bool AnyModelReady() => ModelCatalog.BestDownloaded() != null;

    private void BuildModelList()
    {
        ModelList.Children.Clear();
        _bars.Clear();

        var recommended = ModelCatalog.Recommended();
        var selected = SettingsStore.Instance.Settings.SelectedModel;

        ModelStatusText.Text = Transcriber.Instance.IsLoading
            ? "Loading model…"
            : Transcriber.Instance.IsReady
                ? $"Ready — {ModelCatalog.ById(Transcriber.Instance.CurrentModelId)?.DisplayName} on {Transcriber.Instance.RuntimeLabel}"
                : AnyModelReady()
                    ? "Model found on disk — click Use, or download another."
                    : "";

        // Recommended first, then the rest.
        var ordered = ModelCatalog.All
            .OrderByDescending(m => m.Id == recommended.Id)
            .ThenBy(m => m.MinBytes);

        foreach (var model in ordered)
        {
            var isActive = model.IsDownloaded && selected == model.Id
                && Transcriber.Instance.CurrentModelId == model.Id;

            var info = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal };
            title.Children.Add(new TextBlock
            {
                Text = model.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
            });
            if (model.Id == recommended.Id) title.Children.Add(MiniChip("Recommended", filled: false));
            if (isActive) title.Children.Add(MiniChip("Active", filled: true));
            info.Children.Add(title);
            info.Children.Add(new TextBlock
            {
                Text = $"{model.Blurb}  ·  {model.SizeLabel}",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("MutedBrush"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (ModelDownloader.Instance.IsDownloading(model.Id))
            {
                var bar = new ProgressBar { Width = 120, Height = 7, Minimum = 0, Maximum = 1 };
                _bars[model.Id] = bar;
                actions.Children.Add(bar);
            }
            else if (model.IsDownloaded)
            {
                if (!isActive)
                {
                    var use = new Button
                    {
                        Content = "Use",
                        Style = (Style)FindResource("AccentButton"),
                        Margin = new Thickness(8, 0, 0, 0),
                    };
                    var modelId = model.Id;
                    use.Click += async (_, _) =>
                    {
                        SettingsStore.Instance.Settings.SelectedModel = modelId;
                        SettingsStore.Instance.Save();
                        try { await Transcriber.Instance.LoadModelAsync(modelId); }
                        catch (Exception ex)
                        {
                            MessageBox.Show(this, ex.Message, "FlowType",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                        BuildModelList();
                    };
                    actions.Children.Add(use);
                }
            }
            else
            {
                var download = new Button
                {
                    Content = "Download",
                    Style = (Style)FindResource("AccentButton"),
                    Margin = new Thickness(8, 0, 0, 0),
                };
                var modelId = model.Id;
                download.Click += (_, _) =>
                {
                    _ = ModelDownloader.Instance.DownloadAsync(modelId);
                    BuildModelList();
                };
                actions.Children.Add(download);
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(info);
            grid.Children.Add(actions);

            ModelList.Children.Add(new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                BorderBrush = (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            });
        }
    }

    private void OnProgress(string modelId, double progress) => Dispatcher.BeginInvoke(() =>
    {
        if (_bars.TryGetValue(modelId, out var bar)) bar.Value = progress;
        else BuildModelList();
    });

    private void OnDownloadDone(string modelId, string? error) => Dispatcher.BeginInvoke(() =>
    {
        if (error != null)
        {
            MessageBox.Show(this, $"Download failed:\n{error}", "FlowType",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            var model = ModelCatalog.ById(modelId);
            if (model is { IsDownloaded: true })
            {
                // First model in → make it live immediately.
                SettingsStore.Instance.Settings.SelectedModel = modelId;
                SettingsStore.Instance.Save();
                _ = Transcriber.Instance.LoadModelAsync(modelId);
            }
        }
        BuildModelList();
    });

    private void OnTranscriberChanged() => Dispatcher.BeginInvoke(BuildModelList);

    // ----- Hotkey -----

    private void LoadHotkeys()
    {
        var s = SettingsStore.Instance.Settings;
        HotkeyCombo.Items.Clear();
        foreach (var def in HotkeyDefs.All)
        {
            HotkeyCombo.Items.Add(new ComboBoxItem { Content = def.Display, Tag = def.Id });
        }
        if (s.CustomVks.Length > 0)
        {
            HotkeyCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Custom: {s.CustomLabel}",
                Tag = "Custom",
            });
        }
        var index = 0;
        for (var i = 0; i < HotkeyCombo.Items.Count; i++)
        {
            if ((string)((ComboBoxItem)HotkeyCombo.Items[i]).Tag == s.Hotkey)
            {
                index = i;
                break;
            }
        }
        HotkeyCombo.SelectedIndex = index;
    }

    private void Hotkey_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        if (HotkeyCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            SettingsStore.Instance.Settings.Hotkey = id;
            SettingsStore.Instance.Save();
            UpdatePracticeHint();
        }
    }

    private void UpdatePracticeHint()
    {
        if (_step != 4) return;
        var hotkey = HotkeyDefs.CurrentDisplay(SettingsStore.Instance.Settings);
        PracticeHint.Text = $"Try it now: click the box, hold {hotkey}, and speak.";
    }

    // ----- Detect key -----

    private void DetectKey_Click(object sender, RoutedEventArgs e)
    {
        var hotkeys = App.Hotkeys;
        if (hotkeys == null) return;

        if (_capturing)
        {
            hotkeys.CancelCapture();
            EndCapture("Detection canceled.");
            return;
        }

        _capturing = true;
        DetectKeyButton.Content = "Cancel";
        DetectHint.Text = "Listening… press the key or mouse button you want to hold.";

        hotkeys.CaptureNextKey(vk => Dispatcher.BeginInvoke(() =>
        {
            if (!_capturing) return;
            if (vk == 0x1B)
            {
                EndCapture("Kept the current hotkey.");
                return;
            }

            var s = SettingsStore.Instance.Settings;
            s.Hotkey = "Custom";
            s.CustomVks = new[] { vk };
            s.CustomLabel = VkNames.Name(vk);
            SettingsStore.Instance.Save();

            _loadingControls = true;
            LoadHotkeys();
            _loadingControls = false;

            EndCapture($"Bound to “{VkNames.Name(vk)}”. Hold it in the box below and talk.");
            UpdatePracticeHint();
        }));

        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (!_capturing) return;
            hotkeys.CancelCapture();
            EndCapture("Nothing detected. If you pressed a key, your keyboard software is " +
                "swallowing it — try another key or a mouse button.");
        }));
    }

    private void EndCapture(string hint)
    {
        _capturing = false;
        DetectKeyButton.Content = "Detect key…";
        DetectHint.Text = hint;
    }

    // ----- English variant -----

    private void LoadVariants()
    {
        var current = SettingsStore.Instance.Settings.EnglishVariant;
        VariantCombo.Items.Clear();
        var options = new (string Value, string Label)[]
        {
            (EnglishVariant.Off, "Leave as the AI writes it"),
            (EnglishVariant.Uk, "British spelling"),
            (EnglishVariant.Us, "American spelling"),
        };
        var index = 0;
        for (var i = 0; i < options.Length; i++)
        {
            VariantCombo.Items.Add(new ComboBoxItem
            {
                Content = options[i].Label,
                Tag = options[i].Value,
            });
            if (options[i].Value == current) index = i;
        }
        VariantCombo.SelectedIndex = index;
    }

    private void Variant_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        if (VariantCombo.SelectedItem is ComboBoxItem item && item.Tag is string variant)
        {
            SettingsStore.Instance.Settings.EnglishVariant = variant;
            SettingsStore.Instance.Save();
        }
    }

    /// <summary>Render each wizard step to PNG (hidden --snapshot diagnostic).</summary>
    internal void SnapshotSteps(string dir)
    {
        var root = (FrameworkElement)Content;
        var bg = (Brush)FindResource("BgBrush");
        for (var step = 1; step <= 4; step++)
        {
            _step = step;
            UpdateChrome();
            UpdateLayout();
            Snapshot.Save(root, System.IO.Path.Combine(dir, $"onboarding-{step}.png"), bg);
        }
        AudioRecorder.Instance.StopMonitor();
    }

    private static Border MiniChip(string text, bool filled) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9.5,
            FontWeight = FontWeights.Medium,
            Foreground = filled
                ? new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A))
                : new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
        },
        Background = filled ? Brushes.White : Brushes.Transparent,
        BorderBrush = filled ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 0, 6, 1),
        Margin = new Thickness(7, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
