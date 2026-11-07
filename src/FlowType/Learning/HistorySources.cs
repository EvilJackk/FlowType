namespace FlowType.Learning;

/// <summary>
/// One door for "give me this dictation's recording", whichever history it
/// came from, so the personal benchmark never needs to know where a sample
/// was read.
/// </summary>
public static class HistorySources
{
    /// <summary>
    /// The sample's recording as 16 kHz mono floats in −1..1, or null when it
    /// has none or it can no longer be read. Never throws. The caller owns the
    /// array and should drop it as soon as it is decoded: a minute of audio is
    /// ~4 MB, and nothing here keeps a copy.
    /// </summary>
    /// <param name="sample">A sample from <see cref="FlowTypeHistory"/> or <see cref="WisprHistory"/>.</param>
    /// <param name="wisprPath">The Wispr database the sample was read from; null = the default location.</param>
    public static float[]? LoadAudio(HistorySample sample, string? wisprPath = null)
    {
        if (sample == null || !sample.HasAudio) return null;
        return sample.Source switch
        {
            HistorySource.FlowType => FlowTypeHistory.ReadAudio(sample.Id),
            HistorySource.WisprFlow => WisprHistory.ReadAudio(sample.Id, wisprPath),
            _ => null,
        };
    }
}
