using System.Collections.Concurrent;
using System.IO;
using System.Text;
using FlowType.Core;
using FlowType.Engine;

namespace FlowType.Learning;

/// <summary>
/// The word pieces the installed whisper model reads and writes, taken from
/// the model file itself, as a local answer to "does Whisper already know
/// this word?".
///
/// Whisper writes text as byte-pair tokens. A word the model met constantly
/// in training is one token together with its leading space (" the",
/// " Google", " GitHub"); a word it rarely or never saw is spelled out of
/// fragments (" Re" + "for" + "ger"). The first kind needs no help; the second
/// is what the decoder's ~900-character prompt is worth spending on. Asking
/// the file is exact for the model the user actually runs, needs no word
/// list, and costs half a megabyte of reading.
///
/// The ggml file is laid out as: uint32 magic 0x67676d6c, 11 int32
/// hyper-parameters, int32 n_mel and n_fft, n_mel·n_fft float32 mel filters,
/// int32 token count, then per token an int32 length and that many raw bytes
/// (a leading space is a literal 0x20). The weights follow; they are never
/// read. Verified on large-v3-q5_0, large-v3-turbo, small.en and tiny
/// (50,257 tokens each; n_mels 128 for large-v3, 80 for the others).
///
/// Tokens are raw bytes, sometimes half of a UTF-8 sequence, so they are kept
/// as Latin-1 strings — one char per byte — and every query is turned into its
/// UTF-8 bytes the same way before it is looked up. That makes the comparison
/// byte-exact without decoding anything the model stores.
/// </summary>
public sealed class WhisperVocabulary
{
    private const uint Magic = 0x67676d6c;

    /// <summary>The 11 int32 hyper-parameters after the magic (n_vocab … ftype).</summary>
    private const int HyperParameterCount = 11;

    /// <summary>A real vocabulary has ~50 K entries; far more means the file is not what it claims.</summary>
    private const int MaxTokenCount = 100_000;

    /// <summary>The longest real token is 128 bytes (small.en); a larger length is corruption.</summary>
    private const int MaxTokenBytes = 1024;

    /// <summary>Mel-filter sizes beyond these are not a whisper checkpoint.</summary>
    private const int MaxMels = 512;
    private const int MaxFft = 4096;

    /// <summary>Read in pieces this large; the header and vocabulary are ~0.6 MB in all.</summary>
    private const int BufferBytes = 64 * 1024;

    /// <summary>
    /// One parse per model file, re-read only when the file changes (a new
    /// download under the same name). Failures are not cached: they are cheap,
    /// because a bad header stops the read within the first 60 bytes.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (long Length, DateTime WrittenUtc, WhisperVocabulary Vocabulary)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly HashSet<string> _tokens;
    private readonly int _longestToken;

    private WhisperVocabulary(HashSet<string> tokens)
    {
        _tokens = tokens;
        _longestToken = tokens.Count == 0 ? 0 : tokens.Max(t => t.Length);
    }

    /// <summary>Number of distinct text tokens read from the file.</summary>
    public int Count => _tokens.Count;

    /// <summary>
    /// The vocabulary of the ggml checkpoint at <paramref name="ggmlPath"/>,
    /// or null when the file is missing, unreadable, or not shaped like a
    /// whisper model. Reads only the header and the vocabulary.
    /// </summary>
    public static WhisperVocabulary? TryLoad(string ggmlPath)
    {
        if (string.IsNullOrWhiteSpace(ggmlPath)) return null;
        try
        {
            var full = Path.GetFullPath(ggmlPath);
            var info = new FileInfo(full);
            if (!info.Exists) return null;
            if (Cache.TryGetValue(full, out var hit)
                && hit.Length == info.Length && hit.WrittenUtc == info.LastWriteTimeUtc)
            {
                return hit.Vocabulary;
            }

            var tokens = ReadTokens(full);
            if (tokens == null) return null;
            var vocabulary = new WhisperVocabulary(tokens);
            Cache[full] = (info.Length, info.LastWriteTimeUtc, vocabulary);
            return vocabulary;
        }
        catch
        {
            // A locked, vanished or unreadable model only means "no signal".
            return null;
        }
    }

    /// <summary>
    /// The vocabulary of the speech model the user selected, or of the best
    /// one on disk when that file is missing (the token sets of the multilingual
    /// checkpoints are identical, so any of them answers the question nearly as
    /// well). Null when no model is installed.
    /// </summary>
    public static WhisperVocabulary? ForCurrentModel()
    {
        try
        {
            var selected = ModelCatalog.ById(SettingsStore.Instance.Settings.SelectedModel);
            if (selected != null && File.Exists(selected.FilePath))
            {
                var vocabulary = TryLoad(selected.FilePath);
                if (vocabulary != null) return vocabulary;
            }
            var fallback = ModelCatalog.BestDownloaded();
            return fallback == null ? null : TryLoad(fallback.FilePath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A vocabulary from an explicit token list (--selftest only).</summary>
    internal static WhisperVocabulary FromTokens(IEnumerable<string> tokens) =>
        new(new HashSet<string>(tokens.Where(t => t.Length > 0).Select(ToKey), StringComparer.Ordinal));

    /// <summary>
    /// Whether <paramref name="word"/> is a single token when it follows a
    /// space — the position a word has in running speech. Exact and
    /// case-sensitive: " GitHub" is one token, " github" may not be.
    /// </summary>
    public bool IsSingleToken(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        return _tokens.Contains(ToKey(" " + word));
    }

    /// <summary>
    /// Roughly how many tokens whisper spends on <paramref name="term"/> after
    /// a space: greedy longest match over the vocabulary. Real BPE merges in
    /// rank order and can split differently, so this is a proxy for "how
    /// foreign is this term to the model", not the decoder's exact count.
    /// </summary>
    public int EstimateTokens(string term)
    {
        if (string.IsNullOrEmpty(term)) return 0;
        var key = ToKey(" " + term);
        var count = 0;
        var i = 0;
        while (i < key.Length)
        {
            var step = 1;   // a byte the vocabulary lacks still costs a token
            for (var len = Math.Min(_longestToken, key.Length - i); len > 1; len--)
            {
                if (_tokens.Contains(key.Substring(i, len)))
                {
                    step = len;
                    break;
                }
            }
            i += step;
            count++;
        }
        return count;
    }

    /// <summary>UTF-8 bytes of <paramref name="text"/> as a one-char-per-byte string.</summary>
    private static string ToKey(string text) => Latin1.GetString(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// Parse the header and the token table; null at the first value that does
    /// not fit a whisper checkpoint. Opened shared, read-only: the running app
    /// may hold the same file open for decoding.
    /// </summary>
    private static HashSet<string>? ReadTokens(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, BufferBytes, FileOptions.SequentialScan);
        var length = stream.Length;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        try
        {
            if (length < 4 + 4 * HyperParameterCount + 8) return null;
            if (reader.ReadUInt32() != Magic) return null;

            var nVocab = reader.ReadInt32();
            for (var k = 1; k < HyperParameterCount; k++) reader.ReadInt32();
            if (nVocab is <= 0 or > MaxTokenCount) return null;

            var nMel = reader.ReadInt32();
            var nFft = reader.ReadInt32();
            if (nMel is <= 0 or > MaxMels || nFft is <= 0 or > MaxFft) return null;
            var filterBytes = (long)nMel * nFft * sizeof(float);
            if (stream.Position + filterBytes + 4 > length) return null;
            stream.Seek(filterBytes, SeekOrigin.Current);

            var count = reader.ReadInt32();
            if (count is <= 0 or > MaxTokenCount || count > nVocab) return null;

            var tokens = new HashSet<string>(count, StringComparer.Ordinal);
            var buffer = new byte[MaxTokenBytes];
            for (var i = 0; i < count; i++)
            {
                var size = reader.ReadInt32();
                if (size is < 0 or > MaxTokenBytes) return null;
                if (stream.Position + size > length) return null;
                if (size == 0) continue;
                var read = 0;
                while (read < size)
                {
                    var n = reader.Read(buffer, read, size - read);
                    if (n <= 0) return null;
                    read += n;
                }
                tokens.Add(Latin1.GetString(buffer, 0, size));
            }
            return tokens.Count > 0 ? tokens : null;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }
}
