using System.Buffers.Binary;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Clip;

/// <summary>L2-normalized genre text embeddings (row-major, one row per taxonomy genre).</summary>
internal sealed record ClipGenreEmbeddings(string[] Ids, float[] Matrix, int Dimension, bool FromCache);

internal static class ClipGenreEmbeddingStore
{
    private const int TextBatchSize = 32;
    private static readonly byte[] Magic = "CLIPGEN1"u8.ToArray();

    public static IReadOnlyList<string> PromptsFor(GenreDefinition genre)
    {
        var prompts = new List<string>(genre.Prompts.Count);
        foreach (var prompt in genre.Prompts)
        {
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                prompts.Add(prompt);
            }
        }

        if (prompts.Count == 0)
        {
            prompts.Add("a photo of " + (string.IsNullOrWhiteSpace(genre.Name) ? genre.Id : genre.Name));
        }

        return prompts;
    }

    public static ClipGenreEmbeddings Compute(InferenceSession textSession, ClipTokenizer tokenizer, GenreTaxonomy taxonomy)
    {
        var genres = taxonomy.Genres;
        var prompts = new List<string>();
        var owner = new List<int>();
        for (var g = 0; g < genres.Count; g++)
        {
            foreach (var prompt in PromptsFor(genres[g]))
            {
                prompts.Add(prompt);
                owner.Add(g);
            }
        }

        var inputName = PickName(textSession.InputMetadata.Keys, "input_ids");
        var maskName = FindName(textSession.InputMetadata.Keys, "attention_mask");
        var outputName = PickName(textSession.OutputMetadata.Keys, "text_embeds");

        float[]? sums = null;
        var dimension = 0;
        for (var start = 0; start < prompts.Count; start += TextBatchSize)
        {
            var n = Math.Min(TextBatchSize, prompts.Count - start);
            var ids = new long[n * ClipTokenizer.ContextLength];
            for (var i = 0; i < n; i++)
            {
                tokenizer.Encode(prompts[start + i], ids.AsSpan(i * ClipTokenizer.ContextLength, ClipTokenizer.ContextLength));
            }

            long[] shape = [n, ClipTokenizer.ContextLength];
            var names = new List<string> { inputName };
            using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
            var values = new List<OrtValue> { idsValue };
            OrtValue? maskValue = null;
            try
            {
                if (maskName is not null)
                {
                    maskValue = OrtValue.CreateTensorValueFromMemory(BuildMask(ids, n), shape);
                    names.Add(maskName);
                    values.Add(maskValue);
                }

                using var runOptions = new RunOptions();
                using var outputs = textSession.Run(runOptions, names, values, [outputName]);
                var embeds = outputs[0].GetTensorDataAsSpan<float>();
                if (sums is null)
                {
                    dimension = embeds.Length / n;
                    sums = new float[genres.Count * dimension];
                }

                for (var i = 0; i < n; i++)
                {
                    var row = embeds.Slice(i * dimension, dimension);
                    var norm = Norm(row);
                    var target = sums.AsSpan(owner[start + i] * dimension, dimension);
                    for (var d = 0; d < dimension; d++)
                    {
                        target[d] += row[d] / norm;
                    }
                }
            }
            finally
            {
                maskValue?.Dispose();
            }
        }

        sums ??= [];
        for (var g = 0; g < genres.Count && dimension > 0; g++)
        {
            Normalize(sums.AsSpan(g * dimension, dimension));
        }

        var genreIds = new string[genres.Count];
        for (var g = 0; g < genres.Count; g++)
        {
            genreIds[g] = genres[g].Id;
        }

        return new ClipGenreEmbeddings(genreIds, sums, dimension, FromCache: false);
    }

    public static ClipGenreEmbeddings? TryLoad(string path, IReadOnlyList<GenreDefinition> genres)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            var span = bytes.AsSpan();
            if (span.Length < 16 || !span[..8].SequenceEqual(Magic))
            {
                return null;
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
            var dimension = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
            if (count != genres.Count || dimension <= 0 || span.Length != 16 + ((long)count * dimension * sizeof(float)))
            {
                return null;
            }

            var matrix = new float[count * dimension];
            var data = span[16..];
            for (var i = 0; i < matrix.Length; i++)
            {
                matrix[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * sizeof(float))..]);
            }

            var ids = new string[count];
            for (var g = 0; g < count; g++)
            {
                ids[g] = genres[g].Id;
            }

            return new ClipGenreEmbeddings(ids, matrix, dimension, FromCache: true);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string path, ClipGenreEmbeddings embeddings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[16 + (embeddings.Matrix.Length * sizeof(float))];
        Magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), embeddings.Ids.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), embeddings.Dimension);
        for (var i = 0; i < embeddings.Matrix.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16 + (i * sizeof(float))), embeddings.Matrix[i]);
        }

        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static string TaxonomyHash(GenreTaxonomy taxonomy)
    {
        var builder = new StringBuilder();
        foreach (var genre in taxonomy.Genres)
        {
            builder.Append(genre.Id).Append('\u001e');
            foreach (var prompt in PromptsFor(genre))
            {
                builder.Append(prompt).Append('\u001f');
            }

            builder.Append('\n');
        }

        return ClipHash.Hex(builder.ToString(), 16);
    }

    internal static string PickName(IEnumerable<string> names, string preferred)
    {
        string? first = null;
        foreach (var name in names)
        {
            if (name == preferred)
            {
                return name;
            }

            first ??= name;
        }

        return first ?? throw new InvalidOperationException("ONNX model has no inputs/outputs.");
    }

    private static string? FindName(IEnumerable<string> names, string wanted)
    {
        foreach (var name in names)
        {
            if (name == wanted)
            {
                return name;
            }
        }

        return null;
    }

    private static long[] BuildMask(long[] ids, int rows)
    {
        var mask = new long[ids.Length];
        for (var r = 0; r < rows; r++)
        {
            var offset = r * ClipTokenizer.ContextLength;
            for (var i = 0; i < ClipTokenizer.ContextLength; i++)
            {
                mask[offset + i] = 1;
                if (i > 0 && ids[offset + i] == ClipTokenizer.EndOfText)
                {
                    break;
                }
            }
        }

        return mask;
    }

    internal static float Norm(ReadOnlySpan<float> v)
    {
        var sum = 0.0;
        for (var i = 0; i < v.Length; i++)
        {
            sum += (double)v[i] * v[i];
        }

        return sum > 0 ? (float)Math.Sqrt(sum) : 1f;
    }

    internal static void Normalize(Span<float> v)
    {
        var norm = Norm(v);
        for (var i = 0; i < v.Length; i++)
        {
            v[i] /= norm;
        }
    }
}
