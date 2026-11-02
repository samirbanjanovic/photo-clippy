using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhotoClippy.Plugins.Clip;

/// <summary>CLIP byte-level BPE tokenizer (equivalent to the Hugging Face fast CLIP tokenizer).</summary>
internal sealed partial class ClipTokenizer
{
    public const int ContextLength = 77;
    public const int StartOfText = 49406;
    public const int EndOfText = 49407;
    private const string EndOfWord = "</w>";
    private const int MaxWordCacheEntries = 50_000;

    private static readonly string[] ByteToUnicode = BuildByteToUnicode();

    private readonly Dictionary<string, int> _vocab;
    private readonly Dictionary<string, int> _ranks;
    private readonly ConcurrentDictionary<string, int[]> _cache = new(StringComparer.Ordinal);

    private ClipTokenizer(Dictionary<string, int> vocab, Dictionary<string, int> ranks)
    {
        _vocab = vocab;
        _ranks = ranks;
    }

    public int VocabSize => _vocab.Count;

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex PreTokenizer();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    public static ClipTokenizer Load(string vocabPath, string mergesPath)
    {
        Dictionary<string, int> vocab;
        using (var stream = File.OpenRead(vocabPath))
        {
            vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(stream)
                ?? throw new InvalidDataException("vocab.json is empty.");
        }

        var ranks = new Dictionary<string, int>(50_000, StringComparer.Ordinal);
        var rank = 0;
        foreach (var line in File.ReadLines(mergesPath))
        {
            if (line.Length == 0 || line.StartsWith("#version", StringComparison.Ordinal))
            {
                continue;
            }

            ranks.TryAdd(line, rank++);
        }

        return new ClipTokenizer(vocab, ranks);
    }

    /// <summary>Token ids including start/end markers, truncated to <see cref="ContextLength"/>.</summary>
    public List<int> Tokenize(string text)
    {
        var ids = new List<int>(16) { StartOfText };
        foreach (Match match in PreTokenizer().Matches(Normalize(text)))
        {
            var piece = match.Value;
            if (piece == "<|startoftext|>")
            {
                ids.Add(StartOfText);
                continue;
            }

            if (piece == "<|endoftext|>")
            {
                ids.Add(EndOfText);
                continue;
            }

            ids.AddRange(EncodeWord(piece));
        }

        if (ids.Count > ContextLength - 1)
        {
            ids.RemoveRange(ContextLength - 1, ids.Count - (ContextLength - 1));
        }

        ids.Add(EndOfText);
        return ids;
    }

    /// <summary>Writes <see cref="ContextLength"/> ids into <paramref name="destination"/>, padded with the end marker.</summary>
    public void Encode(string text, Span<long> destination)
    {
        if (destination.Length < ContextLength)
        {
            throw new ArgumentException("Destination is shorter than the context length.", nameof(destination));
        }

        var ids = Tokenize(text);
        for (var i = 0; i < ContextLength; i++)
        {
            destination[i] = i < ids.Count ? ids[i] : EndOfText;
        }
    }

    private static string Normalize(string text)
    {
        string normalized;
        try
        {
            normalized = text.Normalize(NormalizationForm.FormC);
        }
        catch (PlatformNotSupportedException)
        {
            normalized = text;
        }

        return Whitespace().Replace(normalized, " ").ToLowerInvariant();
    }

    private int[] EncodeWord(string piece)
    {
        if (_cache.TryGetValue(piece, out var cached))
        {
            return cached;
        }

        var bytes = Encoding.UTF8.GetBytes(piece);
        var symbols = new List<string>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            symbols.Add(i == bytes.Length - 1 ? ByteToUnicode[bytes[i]] + EndOfWord : ByteToUnicode[bytes[i]]);
        }

        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            var bestIndex = -1;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (_ranks.TryGetValue(symbols[i] + " " + symbols[i + 1], out var r) && r < bestRank)
                {
                    bestRank = r;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                break;
            }

            var first = symbols[bestIndex];
            var second = symbols[bestIndex + 1];
            var merged = first + second;
            var next = new List<string>(symbols.Count);
            for (var i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == first && symbols[i + 1] == second)
                {
                    next.Add(merged);
                    i++;
                }
                else
                {
                    next.Add(symbols[i]);
                }
            }

            symbols = next;
        }

        var ids = new int[symbols.Count];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = _vocab.TryGetValue(symbols[i], out var id) ? id : EndOfText;
        }

        if (_cache.Count < MaxWordCacheEntries)
        {
            _cache.TryAdd(piece, ids);
        }

        return ids;
    }

    private static string[] BuildByteToUnicode()
    {
        var map = new string[256];
        var extra = 0;
        for (var b = 0; b < 256; b++)
        {
            var printable = (b >= '!' && b <= '~') || (b >= 0xA1 && b <= 0xAC) || (b >= 0xAE && b <= 0xFF);
            map[b] = printable ? ((char)b).ToString() : ((char)(256 + extra++)).ToString();
        }

        return map;
    }
}
