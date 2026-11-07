using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace FlowType.UI.Bar;

/// <summary>
/// Pure-logic checks for the flow bar, for <c>--selftest</c>: how session
/// messages are classified into toasts, how the level meter responds to
/// silence, speech and a muted device, and that the hover zone has
/// hysteresis between Resting and Ready. It also carries the few pure-logic
/// checks the main window's pages have (the microphone list), since this is
/// the UI self-test <c>--selftest</c> already runs. No window, no microphone, no timers —
/// the meter is driven with synthetic peaks at the recorder's real cadence
/// (one 50 ms buffer per ~33 ms UI frame, give or take).
/// </summary>
internal static class BarSelfTest
{
    public static IReadOnlyList<(string Name, bool Ok, string Detail)> Run()
    {
        var results = new List<(string, bool, string)>();
        void Check(string name, bool ok, string detail) => results.Add((name, ok, detail));

        // ----- Toast classification -----

        var words = BarToast.From("✓ 42 words", false);
        Check("toast: check mark → success mini",
            words is { Kind: ToastKind.Success, Mini: true, Text: "42 words" }
                && words.Duration == BarToast.MiniDuration,
            $"{words.Kind} mini={words.Mini} '{words.Text}' {words.Duration.TotalSeconds}s");

        var canceled = BarToast.From("Canceled", false);
        Check("toast: short note → info mini",
            canceled is { Kind: ToastKind.Info, Mini: true }, $"{canceled.Kind} mini={canceled.Mini}");

        var muted = BarToast.From("No sound from the microphone — is it muted?", true);
        Check("toast: error → standard 6 s",
            muted is { Kind: ToastKind.Error, Mini: false } && muted.Duration == BarToast.StandardDuration,
            $"{muted.Kind} mini={muted.Mini} {muted.Duration.TotalSeconds}s");

        var learned = BarToast.From("Added to dictionary: kube cuddle → kubectl", false);
        Check("toast: dictionary learn → success standard",
            learned is { Kind: ToastKind.Success, Mini: false }, $"{learned.Kind} mini={learned.Mini}");

        var bare = BarToast.From("✓", false);
        Check("toast: lone check mark keeps its text", bare.Text.Length > 0, $"'{bare.Text}'");

        // ----- Waveform shape -----

        var edge = BarWaveform.Bulge(0);
        var centre = BarWaveform.Bulge(BarWaveform.BarCount / 2);
        Check("waveform: centre bulge 1 − d²/48",
            Math.Abs(edge - (1 - 4.5 * 4.5 / 48)) < 1e-9 && centre > 0.99,
            $"edge={edge:0.000} centre={centre:0.000}");

        // ----- Level meter -----

        const double frame = 0.033;
        double Feed(MicLevelMeter meter, float peak, double seconds)
        {
            var level = meter.Level;
            for (var t = 0.0; t < seconds; t += frame)
            {
                meter.Ingest(peak);
                level = meter.Advance(frame);
            }
            return level;
        }

        var m = new MicLevelMeter();
        m.Reset();
        var room = Feed(m, 0.003f, 1.0);           // ≈ −50 dBFS room tone
        Check("meter: room tone stays near zero", room < 0.05, $"level={room:0.000}");

        var speech = Feed(m, 0.3f, 0.5);           // ≈ −10 dBFS speech peaks
        Check("meter: speech fills the bars", speech > 0.8, $"level={speech:0.000}");
        Check("meter: live signal marks the mic active", m.MicActive, $"active={m.MicActive}");

        var after = Feed(m, 0.003f, 1.0);
        Check("meter: bars fall back after speech", after < 0.1, $"level={after:0.000}");

        var quiet = new MicLevelMeter();
        quiet.Reset();
        Feed(quiet, 0.002f, 0.6);                  // quiet laptop mic floor
        var soft = Feed(quiet, 0.03f, 0.5);        // soft voice ≈ −30 dBFS
        Check("meter: a soft voice on a quiet mic still registers", soft > 0.4, $"level={soft:0.000}");

        var dead = new MicLevelMeter();
        dead.Reset();
        var zero = Feed(dead, 0f, 1.0);
        Check("meter: a muted device (exact zeros) is not active",
            !dead.MicActive && zero == 0, $"active={dead.MicActive} level={zero:0.000}");

        var stalled = new MicLevelMeter();
        stalled.Reset();
        Feed(stalled, 0.002f, 0.5);
        Feed(stalled, 0.3f, 0.3);
        var level = stalled.Level;
        for (var i = 0; i < 30; i++) level = stalled.Advance(frame);   // ~1 s, no buffers
        Check("meter: a stalled device lets the bars fall", level < 0.1, $"level={level:0.000}");

        // ----- Hover hysteresis -----
        // The zone that turned the sliver Ready must still count as "on the
        // bar" once it is Ready (and at every size in between), or a cursor in
        // the resting pad makes the bar flip Resting ↔ Ready forever.

        foreach (var top in new[] { false, true })
        {
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                // Centred on x = 0 and anchored to the screen edge at y = 0,
                // the way the Stage anchors the pill to the bottom (or top).
                Rect PillAt(Size dip) => new(-dip.Width * scale / 2, top ? 0 : -dip.Height * scale,
                    dip.Width * scale, dip.Height * scale);

                var resting = FlowBarWindow.HoverZone(PillAt(FlowBarWindow.RestingSize),
                    FlowBarWindow.BarView.Resting, scale);
                var ready = FlowBarWindow.HoverZone(PillAt(FlowBarWindow.ReadySize),
                    FlowBarWindow.BarView.Ready, scale);
                var midMorph = FlowBarWindow.HoverZone(PillAt(new Size(
                        (FlowBarWindow.RestingSize.Width + FlowBarWindow.ReadySize.Width) / 2,
                        (FlowBarWindow.RestingSize.Height + FlowBarWindow.ReadySize.Height) / 2)),
                    FlowBarWindow.BarView.Ready, scale);
                var where = $"{(top ? "top" : "bottom")} @{scale:0.#}x";
                Check($"hover: Ready zone contains the Resting zone ({where})",
                    ready.Contains(resting) && midMorph.Contains(resting),
                    $"resting={resting} ready={ready} mid={midMorph}");

                // The reported case: in the resting pad, outside the bare 52×30 pill.
                var probe = new Point(30 * scale, top ? 5 * scale : -5 * scale);
                Check($"hover: a cursor in the resting pad stays inside once Ready ({where})",
                    resting.Contains(probe) && ready.Contains(probe)
                        && !PillAt(FlowBarWindow.ReadySize).Contains(probe),
                    $"probe={probe}");
            }
        }

        var listening = new Rect(-48, -30, 96, 30);
        Check("hover: views that do not resize on hover keep the exact pill",
            FlowBarWindow.HoverZone(listening, FlowBarWindow.BarView.Listening, 1) == listening
                && FlowBarWindow.HoverZone(listening, FlowBarWindow.BarView.Toast, 1) == listening,
            $"{FlowBarWindow.HoverZone(listening, FlowBarWindow.BarView.Listening, 1)}");

        // ----- Settings: microphone list -----
        // An unplugged saved mic keeps its own selected entry, so the page's
        // write-everything-back save cannot replace it with "System default".

        string Describe((List<(string Value, string Label)> Items, int Selected) c) =>
            $"[{string.Join(", ", c.Items.Select(i => $"'{i.Value}'"))}] → {c.Selected}";

        var plugged = FlowType.UI.Pages.SettingsPage.MicChoices("Headset (USB)", new[] { "Laptop Mic", "Headset (USB)" });
        Check("settings: a connected saved mic is selected",
            plugged.Items.Count == 3 && plugged.Items[plugged.Selected].Value == "Headset (USB)",
            Describe(plugged));

        var unplugged = FlowType.UI.Pages.SettingsPage.MicChoices("Headset (USB)", new[] { "Laptop Mic" });
        Check("settings: an unplugged saved mic keeps its value, marked not connected",
            unplugged.Items.Count == 3
                && unplugged.Items[unplugged.Selected].Value == "Headset (USB)"
                && unplugged.Items[unplugged.Selected].Label.EndsWith("(not connected)", StringComparison.Ordinal),
            Describe(unplugged));

        var followDefault = FlowType.UI.Pages.SettingsPage.MicChoices("", new[] { "Laptop Mic" });
        var noAudio = FlowType.UI.Pages.SettingsPage.MicChoices(null, Array.Empty<string>());
        Check("settings: no saved mic selects System default, with no placeholder",
            followDefault is { Selected: 0 } && followDefault.Items.Count == 2
                && noAudio is { Selected: 0 } && noAudio.Items.Count == 1 && noAudio.Items[0].Value == "",
            $"{Describe(followDefault)}; {Describe(noAudio)}");

        return results;
    }
}
