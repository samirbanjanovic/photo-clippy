using PhotoClippy.Plugins.Clip;

namespace PhotoClippy.Clip.Tests;

public sealed class ClipTokenizerTests
{
    // Reference ids produced by the Hugging Face CLIP tokenizer (openai/clip-vit-base-patch32).
    public static TheoryData<string, int[]> KnownIds => new()
    {
        { "a photo of a cat", [49406, 320, 1125, 539, 320, 2368, 49407] },
        { "a photo of the moon", [49406, 320, 1125, 539, 518, 3293, 49407] },
        { "a photo of a building's architecture", [49406, 320, 1125, 539, 320, 2307, 568, 4920, 49407] },
        { "Hello, World! It's 2024 — don't panic.", [49406, 3306, 267, 1002, 256, 585, 568, 273, 271, 273, 275, 2005, 847, 713, 14124, 269, 49407] },
        { "the northern lights aurora in the sky", [49406, 518, 5049, 3073, 13500, 530, 518, 2390, 49407] },
        { "  multiple   spaces\tand\nnewlines  ", [49406, 6470, 9006, 537, 1218, 3418, 49407] },
        { "café naïve résumé", [49406, 15304, 1097, 35689, 563, 29106, 7054, 4166, 49407] },
        { "rock'n'roll!!!", [49406, 2172, 262, 333, 262, 3341, 995, 49407] },
        { "a photo of 3 dogs & 12 cats (cute) :)", [49406, 320, 1125, 539, 274, 3255, 261, 272, 273, 3989, 263, 2242, 264, 1408, 49407] },
        { "emoji 📷 camera", [49406, 16327, 5551, 3934, 49407] },
        { "We'LL see; they'RE here, I'm sure you've, he'd", [49406, 649, 1342, 862, 282, 889, 982, 763, 267, 328, 880, 1650, 592, 1200, 267, 797, 1896, 49407] },
    };

    [TokenizerTheory]
    [MemberData(nameof(KnownIds))]
    public void Tokenize_MatchesReferenceIds(string text, int[] expected)
    {
        var tokenizer = ClipTestAssets.LoadTokenizer();

        Assert.Equal(expected, tokenizer.Tokenize(text));
    }

    [TokenizerFact]
    public void Encode_PadsToContextLengthWithEndMarker()
    {
        var tokenizer = ClipTestAssets.LoadTokenizer();
        var ids = new long[ClipTokenizer.ContextLength];

        tokenizer.Encode("a photo of a cat", ids);

        Assert.Equal([49406L, 320, 1125, 539, 320, 2368, 49407], ids[..7]);
        Assert.All(ids[7..], id => Assert.Equal(ClipTokenizer.EndOfText, id));
        Assert.Equal(49408, tokenizer.VocabSize);
    }

    [TokenizerFact]
    public void Tokenize_TruncatesLongTextKeepingEndMarker()
    {
        var tokenizer = ClipTestAssets.LoadTokenizer();

        var ids = tokenizer.Tokenize(string.Join(' ', Enumerable.Repeat("cat", 200)));

        Assert.Equal(ClipTokenizer.ContextLength, ids.Count);
        Assert.Equal(ClipTokenizer.StartOfText, ids[0]);
        Assert.Equal(ClipTokenizer.EndOfText, ids[^1]);
        Assert.All(ids[1..^1], id => Assert.Equal(2368, id));
    }

    [Fact]
    public void Tokenize_SyntheticVocabulary_AppliesMergesInRankOrder()
    {
        var dir = ClipTestAssets.NewStateDirectory("tokenizer");
        var vocab = Path.Combine(dir, "vocab.json");
        var merges = Path.Combine(dir, "merges.txt");
        File.WriteAllText(vocab, """{"c": 1, "a": 2, "t</w>": 3, "ca": 4, "cat</w>": 5, "a</w>": 6, "!</w>": 7, "<|startoftext|>": 49406, "<|endoftext|>": 49407}""");
        File.WriteAllText(merges, "#version: 0.2\nc a\nca t</w>\n");
        var tokenizer = ClipTokenizer.Load(vocab, merges);

        Assert.Equal([49406, 5, 6, 7, 49407], tokenizer.Tokenize("CAT a !"));
        Assert.Equal([49406, 2, 1, 3, 49407], tokenizer.Tokenize("act"));
        Assert.Equal([49406, ClipTokenizer.EndOfText, 49407], tokenizer.Tokenize("x"));
        Assert.Equal([49406, 49406, 5, 49407, 49407], tokenizer.Tokenize("<|startoftext|>cat<|endoftext|>"));
    }
}
