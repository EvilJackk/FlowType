using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;
using FlowType.UI.Controls;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI.Pages;

/// <summary>
/// The optional local language model: what it is for, where it runs, which
/// one is installed, and the two features it powers (AI polish and Command
/// Mode). Nothing on this page sends anything anywhere; the only network use
/// is the one-time model download, which LlmDownloader hash-checks.
/// </summary>
public partial class AiPage : IMainPage
{
    private readonly Dictionary<string, ProgressBar> _bars = new();
    private readonly Dictionary<string, (string Text, bool Error)> _rowStatus = new();
    private readonly HashSet<string> _verifying = new();

    /// <summary>
    /// Models whose Delete is in flight. Deleting first waits for the model's
    /// lock (a completion can hold it for seconds), and while it waits the
    /// row's other buttons would start work that makes the delete fail: Verify
    /// opens the file without delete-sharing, Use memory-maps it again.
    /// </summary>
    private readonly HashSet<string> _deleting = new();
    private bool _loading = true;
    private bool _shown;
    private bool _refreshQueued;

    public AiPage()
    {
        InitializeComponent();
        FillBullets(LocalList, "MutedBrush",
            "Speech is transcribed by Whisper on your GPU or CPU",
            "Polish and Command Mode run on a local model",
            "The text box you're in is read locally and never stored",
            "Works with the network cable pulled out");
        // What a cloud dictation service does with the same dictation — the
        // contrast this page exists to make (see the 2.0 Wispr Flow findings).
        FillBullets(CloudList, "FaintBrush",
            "Upload your recording to be transcribed",
            "Send the text around your cursor along with it",
            "Can send screenshots of your screen for context",
            "Can't transcribe until you're back online");
        LoadControls();
        _loading = false;

        LlmDownloader.Instance.ProgressChanged += OnDownloadProgress;
        LlmDownloader.Instance.Completed += OnDownloadCompleted;
        LocalLlm.Instance.StateChanged += OnStateChanged;
        SettingsStore.Instance.Changed += OnStateChanged;
    }

    public FrameworkElement SnapshotBody => Body;

    public void OnShown()
    {
        _shown = true;
        Refresh();
    }

    public void OnHidden() => _shown = false;

    public void Detach()
    {
        LlmDownloader.Instance.ProgressChanged -= OnDownloadProgress;
        LlmDownloader.Instance.Completed -= OnDownloadCompleted;
        LocalLlm.Instance.StateChanged -= OnStateChanged;
        SettingsStore.Instance.Changed -= OnStateChanged;
    }

    private void OnStateChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (!_shown) return;
            LoadControls();
            Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>Bulleted lines with a hanging indent, so wrapped lines align with the text.</summary>
    private static void FillBullets(System.Windows.Controls.Panel panel, string brushKey, params string[] lines)
    {
        foreach (var line in lines)
        {
            var row = Ui.Columns(new GridLength(16), Ui.Star);
            row.Margin = new Thickness(0, 0, 0, 6);
            var bullet = Ui.Text("•", 14, brushKey);
            bullet.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(bullet.At(0));
            row.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 14,
                LineHeight = 21,
                Foreground = Ui.BrushOf(brushKey),
                TextWrapping = TextWrapping.Wrap,
            }.At(1));
            panel.Children.Add(row);
        }
    }

    /// <summary>Settings → controls, without the change handlers writing them back.</summary>
    private void LoadControls()
    {
        var was = _loading;
        _loading = true;
        try
        {
            var s = SettingsStore.Instance.Settings;
            PolishOffRadio.IsChecked = s.AiPolish == "off";
            PolishAlwaysRadio.IsChecked = s.AiPolish == "always";
            PolishSmartRadio.IsChecked = s.AiPolish is not ("off" or "always");
            CommandModeCheck.IsChecked = s.CommandMode;
            BuildCommandKeys(s);
        }
        finally
        {
            _loading = was;
        }
    }

    private void Refresh()
    {
        UpdateStatus();
        RefreshModels();
        NoModelNote.Visibility = LlmCatalog.All.Any(m => m.IsDownloaded) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Polish_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = SettingsStore.Instance.Settings;
        s.AiPolish = PolishOffRadio.IsChecked == true ? "off"
            : PolishAlwaysRadio.IsChecked == true ? "always" : "smart";
        SettingsStore.Instance.Save();
    }

    private void CommandMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Instance.Settings.CommandMode = CommandModeCheck.IsChecked == true;
        SettingsStore.Instance.Save();
    }

    /// <summary>The chord drawn as key caps: [Ctrl] + [Win] + [Alt], then "speak".</summary>
    private void BuildCommandKeys(AppSettings s)
    {
        CommandKeys.Children.Clear();
        var keys = HotkeyDefs.CurrentDisplay(s)
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Append("Alt")
            .ToArray();
        CommandKeys.Children.Add(Ui.Keys("hold together, then speak your instruction", keys));
    }

    // ----- Status -----

    private void UpdateStatus()
    {
        var llm = LocalLlm.Instance;
        var s = SettingsStore.Instance.Settings;
        var selected = LlmCatalog.ById(s.AiModel);

        if (llm.IsLoading)
        {
            LlmStatusDot.Fill = Ui.BrushOf("FaintBrush");
            LlmStatusText.Text = $"Loading {selected?.DisplayName ?? "the model"}…";
            LlmStatusDetail.Text = "The first load prepares the graphics card — usually a few seconds.";
        }
        else if (llm.IsReady)
        {
            LlmStatusDot.Fill = Ui.BrushOf("AccentBrush");
            LlmStatusText.Text = $"{llm.CurrentModel?.DisplayName ?? llm.CurrentModelId} is ready";
            LlmStatusDetail.Text = $"Running on {llm.RuntimeLabel}"
                + (llm.LastCompletionMs > 0 ? $"  ·  last answer in {llm.LastCompletionMs:N0} ms" : "")
                + (s.AiPolish == "off" ? "  ·  AI polish is off" : "");
        }
        else if (llm.LastLoadError != null)
        {
            LlmStatusDot.Fill = Ui.BrushOf("DangerBrush");
            LlmStatusText.Text = "Couldn't load the AI model";
            LlmStatusDetail.Text = llm.LastLoadError;
        }
        else if (!LlmCatalog.All.Any(m => m.IsDownloaded))
        {
            LlmStatusDot.Fill = Ui.BrushOf("FaintBrush");
            LlmStatusText.Text = "No AI model installed";
            LlmStatusDetail.Text = "FlowType works fully without one — formatting uses rules. Add one below for AI polish and Command Mode.";
        }
        else
        {
            LlmStatusDot.Fill = Ui.BrushOf("FaintBrush");
            LlmStatusText.Text = "AI model not in use";
            LlmStatusDetail.Text = "Click Use on a downloaded model below.";
        }
    }

    // ----- Models -----

    private void RefreshModels()
    {
        LlmList.Children.Clear();
        _bars.Clear();
        var recommended = LlmCatalog.Recommended();
        foreach (var model in LlmCatalog.All)
        {
            LlmList.Children.Add(BuildModelRow(model, model.Id == recommended.Id));
        }
    }

    private Border BuildModelRow(LlmModel model, bool isRecommended)
    {
        var llm = LocalLlm.Instance;
        var downloading = LlmDownloader.Instance.IsDownloading(model.Id);
        var downloaded = model.IsDownloaded;
        var isActive = downloaded && llm.CurrentModelId == model.Id && (llm.IsReady || llm.IsLoading);

        var info = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(Ui.Text(model.DisplayName, 15.5, "TextBrush", FontWeights.Bold));
        if (isRecommended) titleRow.Children.Add(Ui.Chip("Recommended for this PC"));
        if (isActive) titleRow.Children.Add(Ui.Chip(llm.IsLoading ? "Loading" : "In use", ChipTone.Accent));
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = model.Blurb,
            Style = Ui.StyleOf("Hint"),
            Margin = new Thickness(0, 5, 0, 0),
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{model.SizeLabel}  ·  wants {model.MinVramGB} GB of graphics memory for GPU speed  ·  {model.Repository}",
            FontSize = 12.5,
            Foreground = Ui.BrushOf("FaintBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var verifying = _verifying.Contains(model.Id);
        var deleting = _deleting.Contains(model.Id);
        if (deleting)
        {
            info.Children.Add(StatusLine("Deleting…", "MutedBrush"));
        }
        else if (verifying)
        {
            info.Children.Add(StatusLine("Checking the file against the published checksum…", "MutedBrush"));
        }
        else if (_rowStatus.TryGetValue(model.Id, out var status))
        {
            info.Children.Add(StatusLine(status.Text, status.Error ? "DangerBrush" : "AccentBrush"));
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        if (downloading)
        {
            var bar = new ProgressBar { Width = 140, Height = 6, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center };
            _bars[model.Id] = bar;
            actions.Children.Add(bar);
            var cancel = Ui.TextButton("Cancel", "GhostButton", 12);
            cancel.Click += (_, _) => LlmDownloader.Instance.Cancel(model.Id);
            actions.Children.Add(cancel);
        }
        else if (downloaded)
        {
            if (!isActive)
            {
                var use = Ui.TextButton("Use", "AccentButton", 0);
                use.IsEnabled = !deleting;
                use.Click += (_, _) => UseModel(model.Id);
                actions.Children.Add(use);
            }
            var verify = Ui.TextButton("Verify", "GhostButton", isActive ? 0 : 8);
            verify.IsEnabled = !verifying && !deleting;
            verify.ToolTip = "Re-read the file and compare it with the published SHA-256";
            verify.Click += (_, _) => Verify(model);
            actions.Children.Add(verify);
            var delete = Ui.TextButton("Delete", "DangerButton");
            // Verify holds the file open for reading, which makes Windows refuse
            // the delete — so Delete waits for the check to finish.
            delete.IsEnabled = !verifying && !deleting;
            if (verifying) ToolTipService.SetShowOnDisabled(delete, true);
            delete.ToolTip = verifying ? "Wait for the checksum check to finish" : null;
            delete.Click += (_, _) => DeleteModel(model);
            actions.Children.Add(delete);
        }
        else
        {
            var download = Ui.TextButton($"Download · {model.SizeLabel}", "AccentButton", 0);
            download.Click += (_, _) =>
            {
                _rowStatus.Remove(model.Id);
                _ = LlmDownloader.Instance.DownloadAsync(model.Id);
                RefreshModels();
            };
            actions.Children.Add(download);
        }

        var grid = Ui.Columns(Ui.Star, Ui.Auto);
        grid.Children.Add(info.At(0));
        grid.Children.Add(actions.At(1));

        var row = new Border { Style = Ui.StyleOf("InsetRow"), Child = grid };
        if (isActive) row.BorderBrush = Ui.BrushOf("AccentLineBrush");
        return row;
    }

    private static TextBlock StatusLine(string text, string brushKey) => new()
    {
        Text = text,
        FontSize = 12.5,
        Foreground = Ui.BrushOf(brushKey),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
    };

    private async void UseModel(string modelId)
    {
        _rowStatus.Remove(modelId);
        SettingsStore.Instance.Settings.AiModel = modelId;
        SettingsStore.Instance.Save();
        RefreshModels();
        try
        {
            await LocalLlm.Instance.LoadAsync(modelId);
        }
        catch (Exception ex)
        {
            // LocalLlm keeps the message too (LastLoadError) for the status card.
            _rowStatus[modelId] = ($"Couldn't load it: {ex.Message}", true);
        }
        if (_shown) Refresh();
    }

    private async void Verify(LlmModel model)
    {
        if (_deleting.Contains(model.Id) || !_verifying.Add(model.Id)) return;
        _rowStatus.Remove(model.Id);
        RefreshModels();
        try
        {
            var ok = await LlmDownloader.VerifyAsync(model);
            _rowStatus[model.Id] = ok
                ? ("Checksum matches the published file.", false)
                : ("Checksum does not match the published file — delete it and download again.", true);
        }
        catch (Exception ex)
        {
            _rowStatus[model.Id] = ($"Couldn't read the file: {ex.Message}", true);
        }
        finally
        {
            _verifying.Remove(model.Id);
        }
        if (_shown) RefreshModels();
    }

    private async void DeleteModel(LlmModel model)
    {
        var answer = Ui.Message(this,
            $"Delete {model.DisplayName} ({model.SizeLabel})? You can download it again any time.",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        // The box was modal, but a Verify could have been started before it opened.
        if (_verifying.Contains(model.Id) || !_deleting.Add(model.Id)) return;

        var llm = LocalLlm.Instance;
        var wasLoaded = llm.CurrentModelId == model.Id;
        _rowStatus.Remove(model.Id);
        RefreshModels();

        string? failure = null;
        try
        {
            await LlmDownloader.Instance.DeleteAsync(model.Id);

            // Only once the file is really gone does the setting stop naming
            // it. Clearing it on a failed delete would leave a model on disk
            // that nothing uses until the user notices and clicks Use.
            var s = SettingsStore.Instance.Settings;
            if (s.AiModel == model.Id)
            {
                s.AiModel = "";
                SettingsStore.Instance.Save();
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            _rowStatus[model.Id] = ($"Couldn't delete it: {ex.Message}", true);
        }
        finally
        {
            _deleting.Remove(model.Id);
        }
        Refresh();
        if (failure == null) return;

        // DeleteAsync unloads before it deletes, and nothing reloads a model
        // lazily — so a failed delete would otherwise leave AI polish and
        // Command Mode silently off until the next start. The file is still
        // there: put the model back, unless the user has picked another one
        // meanwhile. (LocalLlm.StateChanged shows "Loading" on the row.)
        if (wasLoaded && model.IsDownloaded && !llm.IsReady && !llm.IsLoading
            && SettingsStore.Instance.Settings.AiModel == model.Id)
        {
            try
            {
                await llm.LoadAsync(model.Id);
            }
            catch
            {
                // LocalLlm keeps the reason (LastLoadError) for the status card.
            }
            if (_shown) Refresh();
        }
    }

    private void OnDownloadProgress(string modelId, double progress) => Dispatcher.BeginInvoke(() =>
    {
        if (_bars.TryGetValue(modelId, out var bar)) bar.Value = progress;
        else if (_shown) RefreshModels();
    });

    private void OnDownloadCompleted(string modelId, string? error) => Dispatcher.BeginInvoke(() =>
    {
        if (error != null)
        {
            _rowStatus[modelId] = ($"Download failed: {error}", true);
        }
        else if (LlmCatalog.ById(modelId) is { IsDownloaded: true } model)
        {
            _rowStatus[modelId] = ("Downloaded and verified against the published checksum.", false);
            // First model in: put it to work, the way the speech model does.
            if (string.IsNullOrEmpty(SettingsStore.Instance.Settings.AiModel))
            {
                UseModel(model.Id);
                return;
            }
        }
        if (_shown) Refresh();
    });

    private void OpenLlmFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LlmDir));
        }
        catch (Exception ex)
        {
            Ui.Message(this, $"Couldn't open the folder:\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
