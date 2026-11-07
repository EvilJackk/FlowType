using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlowType.Core;
using FlowType.Engine;
using FlowType.UI.Controls;
using RadioButton = System.Windows.Controls.RadioButton;
using TextWrapping = System.Windows.TextWrapping;

namespace FlowType.UI.Pages;

/// <summary>
/// Writing style per kind of app: four buckets (personal messages, work
/// messages, email, everything else), four styles each. The choice is saved
/// to Settings.StylePersonal / StyleWork / StyleEmail / StyleOther and read by
/// <see cref="WritingStyles.For"/> at insertion time.
/// </summary>
public partial class StylePage : IMainPage
{
    /// <summary>
    /// Which apps land in each bucket, in words. Mirrors AppCategory's exact
    /// process-name table (only the apps users recognise by name are listed).
    /// </summary>
    private static readonly Dictionary<WritingStyles.Bucket, string> BucketApps = new()
    {
        [WritingStyles.Bucket.Personal] = "Discord, WhatsApp, Telegram, Signal and Messenger",
        [WritingStyles.Bucket.Work] = "Slack, Microsoft Teams, Zoom and Webex",
        [WritingStyles.Bucket.Email] = "Outlook, Thunderbird, Mailspring, Superhuman and HEY",
        [WritingStyles.Bucket.Other] = "everything else — documents, code editors, browsers and AI chats",
    };

    /// <summary>What each style does, in one line, under its name.</summary>
    private static readonly Dictionary<string, string> Summaries = new()
    {
        [WritingStyles.Polished] = "Full capitals and punctuation",
        [WritingStyles.Casual] = "Capitals kept, no final full stop",
        [WritingStyles.VeryCasual] = "Lowercase except names, no final full stop",
        [WritingStyles.Excited] = "Ends on an exclamation mark",
    };

    private bool _loading;
    private bool _shown;

    public StylePage()
    {
        InitializeComponent();
        SettingsStore.Instance.Changed += OnExternalChange;
        LocalLlm.Instance.StateChanged += OnExternalChange;
    }

    public FrameworkElement SnapshotBody => Body;

    public void OnShown()
    {
        _shown = true;
        BuildCards();
        UpdateAiStatus();
    }

    public void OnHidden() => _shown = false;

    public void Detach()
    {
        SettingsStore.Instance.Changed -= OnExternalChange;
        LocalLlm.Instance.StateChanged -= OnExternalChange;
    }

    private void OnExternalChange() => Dispatcher.BeginInvoke(() =>
    {
        if (_shown) UpdateAiStatus();
    }, DispatcherPriority.Background);

    private WritingStyles.Bucket CurrentBucket =>
        TabWork.IsChecked == true ? WritingStyles.Bucket.Work
        : TabEmail.IsChecked == true ? WritingStyles.Bucket.Email
        : TabOther.IsChecked == true ? WritingStyles.Bucket.Other
        : WritingStyles.Bucket.Personal;

    private static string Get(AppSettings s, WritingStyles.Bucket bucket) => WritingStyles.Normalize(bucket switch
    {
        WritingStyles.Bucket.Personal => s.StylePersonal,
        WritingStyles.Bucket.Work => s.StyleWork,
        WritingStyles.Bucket.Email => s.StyleEmail,
        _ => s.StyleOther,
    });

    private static void Set(AppSettings s, WritingStyles.Bucket bucket, string style)
    {
        switch (bucket)
        {
            case WritingStyles.Bucket.Personal: s.StylePersonal = style; break;
            case WritingStyles.Bucket.Work: s.StyleWork = style; break;
            case WritingStyles.Bucket.Email: s.StyleEmail = style; break;
            default: s.StyleOther = style; break;
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_shown) BuildCards();
    }

    /// <summary>The four style cards for the selected bucket, 2 × 2, current choice ticked.</summary>
    private void BuildCards()
    {
        var bucket = CurrentBucket;
        BucketHint.Text = $"{WritingStyles.BucketLabel(bucket)} — used in {BucketApps[bucket]}.";

        _loading = true;
        try
        {
            StyleCards.Children.Clear();
            var current = Get(SettingsStore.Instance.Settings, bucket);
            for (var i = 0; i < WritingStyles.All.Length; i++)
            {
                var (id, label, example) = WritingStyles.All[i];
                var card = new RadioButton
                {
                    Style = Ui.StyleOf("ChoiceCard"),
                    GroupName = "StyleChoice",
                    IsChecked = id == current,
                    Content = CardContent(label, Summaries.GetValueOrDefault(id, ""), example),
                };
                System.Windows.Automation.AutomationProperties.SetName(card, $"{label} style");
                card.Checked += (_, _) => Choose(id);
                Grid.SetColumn(card, (i % 2) * 2);
                Grid.SetRow(card, (i / 2) * 2);
                StyleCards.Children.Add(card);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private static StackPanel CardContent(string label, string summary, string example)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = Ui.BrushOf("TextBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = summary,
            Style = Ui.StyleOf("Hint"),
            Margin = new Thickness(0, 3, 0, 14),
        });
        // The example as a message bubble: what a message in this style looks like.
        panel.Children.Add(new Border
        {
            Background = Ui.BrushOf("HoverBrush"),
            CornerRadius = new CornerRadius(14, 14, 14, 4),
            Padding = new Thickness(14, 10, 14, 11),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = example,
                FontSize = 14,
                LineHeight = 20,
                Foreground = Ui.BrushOf("SoftTextBrush"),
                TextWrapping = TextWrapping.Wrap,
            },
        });
        return panel;
    }

    private void Choose(string style)
    {
        if (_loading) return;
        var s = SettingsStore.Instance.Settings;
        Set(s, CurrentBucket, style);
        SettingsStore.Instance.Save();
    }

    private void UpdateAiStatus()
    {
        var s = SettingsStore.Instance.Settings;
        var llm = LocalLlm.Instance;
        AiStatusText.Text = s.AiPolish == "off"
            ? "AI polish is off — styles are applied by the rules only."
            : llm.IsReady
                ? $"AI polish is {(s.AiPolish == "always" ? "on for every dictation" : "on (smart)")} with {llm.CurrentModel?.DisplayName ?? "the local model"}."
                : llm.IsLoading
                    ? "The AI model is loading…"
                    : "AI polish is set, but no on-device model is installed yet — the rules still apply.";
    }

    private void AiSettings_Click(object sender, RoutedEventArgs e) =>
        (Ui.OwnerOf(this) as MainWindow)?.NavigateTo(MainPage.Ai);
}
