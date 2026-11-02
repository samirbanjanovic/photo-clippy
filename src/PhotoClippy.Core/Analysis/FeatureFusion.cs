using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Analysis;

public sealed record GenreAssignment(string? Genre, double Confidence, bool Junk, IReadOnlyDictionary<string, double> Probabilities);

/// <summary>Merges the outputs of several analyzers of the same component.</summary>
public static class FeatureFusion
{
    /// <summary>
    /// Composition: scalars take the first non-null value in priority order; genre probabilities are a weighted
    /// average over the analyzers that produced genres; extras are a union (first wins on conflicts).
    /// </summary>
    public static CompositionFeatures? FuseComposition(IReadOnlyList<(string PluginId, CompositionFeatures Features)> parts, IReadOnlyDictionary<string, double> genreWeights)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Count == 1)
        {
            return parts[0].Features;
        }

        Dictionary<string, double>? genres = null;
        double weightSum = 0;
        foreach (var (id, features) in parts)
        {
            if (features.Genres is not { Count: > 0 } g)
            {
                continue;
            }

            var w = genreWeights.TryGetValue(id, out var configured) ? configured : 1.0;
            if (w <= 0)
            {
                continue;
            }

            genres ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            weightSum += w;
            foreach (var (genre, p) in g)
            {
                if (double.IsFinite(p))
                {
                    genres[genre] = genres.GetValueOrDefault(genre) + (w * p);
                }
            }
        }

        if (genres is not null && weightSum > 0)
        {
            foreach (var key in genres.Keys.ToList())
            {
                genres[key] /= weightSum;
            }
        }

        var extras = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (_, features) in parts)
        {
            foreach (var (k, v) in features.Extras)
            {
                extras.TryAdd(k, v);
            }
        }

        T? First<T>(Func<CompositionFeatures, T?> selector) where T : struct
        {
            foreach (var (_, features) in parts)
            {
                if (selector(features) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        return new CompositionFeatures
        {
            Genres = genres,
            SubjectX = First(f => f.SubjectX),
            SubjectY = First(f => f.SubjectY),
            SubjectArea = First(f => f.SubjectArea),
            ThirdsScore = First(f => f.ThirdsScore),
            CenterScore = First(f => f.CenterScore),
            SymmetryScore = First(f => f.SymmetryScore),
            NegativeSpace = First(f => f.NegativeSpace),
            SeparationScore = First(f => f.SeparationScore),
            HorizonTiltDeg = First(f => f.HorizonTiltDeg),
            HorizonY = First(f => f.HorizonY),
            HorizontalEdges = First(f => f.HorizontalEdges),
            VerticalEdges = First(f => f.VerticalEdges),
            DiagonalEdges = First(f => f.DiagonalEdges),
            Extras = extras,
        };
    }

    /// <summary>Color: every field takes the first non-null value in priority order; extras are a union.</summary>
    public static ColorFeatures? FuseColor(IReadOnlyList<(string PluginId, ColorFeatures Features)> parts)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Count == 1)
        {
            return parts[0].Features;
        }

        T? V<T>(Func<ColorFeatures, T?> selector) where T : struct
        {
            foreach (var (_, f) in parts)
            {
                if (selector(f) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        T? R<T>(Func<ColorFeatures, T?> selector) where T : class
        {
            foreach (var (_, f) in parts)
            {
                if (selector(f) is { } value)
                {
                    return value;
                }
            }

            return null;
        }

        var extras = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (_, f) in parts)
        {
            foreach (var (k, v) in f.Extras)
            {
                extras.TryAdd(k, v);
            }
        }

        return new ColorFeatures
        {
            MeanLuma = V(f => f.MeanLuma),
            LumaP05 = V(f => f.LumaP05),
            LumaP50 = V(f => f.LumaP50),
            LumaP95 = V(f => f.LumaP95),
            ShadowClip = V(f => f.ShadowClip),
            HighlightClip = V(f => f.HighlightClip),
            Contrast = V(f => f.Contrast),
            Key = R(f => f.Key),
            Colorfulness = V(f => f.Colorfulness),
            MeanChroma = V(f => f.MeanChroma),
            Saturation = V(f => f.Saturation),
            Monochrome = V(f => f.Monochrome),
            Warmth = V(f => f.Warmth),
            Tint = V(f => f.Tint),
            Sharpness = V(f => f.Sharpness),
            HueHistogram = R(f => f.HueHistogram),
            LumaHistogram = R(f => f.LumaHistogram),
            Palette = R(f => f.Palette),
            Extras = extras,
        };
    }

    /// <summary>Top-1 genre with a confidence floor; junk genres at or above <paramref name="junkThreshold"/> mark the capture as junk.</summary>
    public static GenreAssignment AssignGenre(IReadOnlyDictionary<string, double>? genres, GenreTaxonomy taxonomy, double minConfidence, double junkThreshold)
    {
        if (genres is not { Count: > 0 })
        {
            return new GenreAssignment(null, 0, false, new Dictionary<string, double>());
        }

        double junk = 0;
        string? best = null;
        double bestP = 0;
        foreach (var (genre, p) in genres)
        {
            var def = taxonomy.Find(genre);
            if (def?.Junk == true)
            {
                junk += p;
            }

            if (p > bestP)
            {
                bestP = p;
                best = def?.Id ?? genre;
            }
        }

        if (junk >= junkThreshold)
        {
            return new GenreAssignment(best, bestP, true, genres);
        }

        return bestP >= minConfidence ? new GenreAssignment(best, bestP, false, genres) : new GenreAssignment(null, bestP, false, genres);
    }
}
