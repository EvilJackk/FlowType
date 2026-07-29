using System.IO;
using System.Media;
using FlowType.Core;

namespace FlowType.Audio;

/// <summary>
/// Quiet synthesized chimes: rising two-tone on record start, falling on stop,
/// short low blip on cancel. Gated by the "Play sounds" setting.
/// </summary>
public static class SoundCues
{
    private const int SampleRate = 22050;
    private const double Amplitude = 0.16;

    private static readonly Lazy<byte[]> StartChime =
        new(() => Build((587.33, 0.055), (880.00, 0.09)));
    private static readonly Lazy<byte[]> StopChime =
        new(() => Build((880.00, 0.055), (587.33, 0.09)));
    private static readonly Lazy<byte[]> CancelBlip =
        new(() => Build((330.00, 0.09)));

    public static void Start() => Play(StartChime.Value);
    public static void Stop() => Play(StopChime.Value);
    public static void Cancel() => Play(CancelBlip.Value);

    private static void Play(byte[] wav)
    {
        if (!SettingsStore.Instance.Settings.PlaySounds) return;
        Task.Run(() =>
        {
            try
            {
                using var player = new SoundPlayer(new MemoryStream(wav));
                player.PlaySync();
            }
            catch { /* no output device — a missing chime is not an error */ }
        });
    }

    private static byte[] Build(params (double Frequency, double Seconds)[] notes)
    {
        var totalSamples = notes.Sum(n => (int)(SampleRate * n.Seconds));
        var dataBytes = totalSamples * 2;

        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);

        foreach (var (frequency, seconds) in notes)
        {
            var count = (int)(SampleRate * seconds);
            var fade = Math.Max(1, (int)(SampleRate * 0.008));
            for (var i = 0; i < count; i++)
            {
                var envelope = 1.0;
                if (i < fade) envelope = i / (double)fade;
                else if (i > count - fade) envelope = (count - i) / (double)fade;

                var sample = Amplitude * envelope
                    * Math.Sin(2 * Math.PI * frequency * i / SampleRate);
                w.Write((short)(sample * short.MaxValue));
            }
        }

        w.Flush();
        return ms.ToArray();
    }
}
