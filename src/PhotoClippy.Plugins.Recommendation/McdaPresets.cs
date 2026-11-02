using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Recommendation;

/// <summary>
/// Preset weight tables. Weights are renormalized over the criteria that are active for a run
/// (portability/value only when every candidate has a weight/price; low-light/isolation only with aperture data;
/// genre only with genre labels).
/// </summary>
public static class McdaPresets
{
    public const string Balanced = "balanced";
    public const string LowLight = "low-light";
    public const string Reach = "reach";
    public const string Versatility = "versatility";
    public const string Portability = "portability";
    public const string Value = "value";

    public static IReadOnlyList<string> All { get; } = [Balanced, LowLight, Reach, Versatility, Portability, Value];

    /// <summary>Criterion order used by the internal weight vectors.</summary>
    public static IReadOnlyList<string> CriterionOrder { get; } =
    [
        CriterionIds.Fit,
        CriterionIds.LowLight,
        CriterionIds.Isolation,
        CriterionIds.Reach,
        CriterionIds.Genre,
        CriterionIds.Portability,
        CriterionIds.Value,
    ];

    private static readonly Dictionary<string, double[]> Tables = new(StringComparer.OrdinalIgnoreCase)
    {
        //                     fit   lowLight isolation reach genre portability value
        [Balanced] = [0.30, 0.25, 0.10, 0.15, 0.10, 0.05, 0.05],
        [LowLight] = [0.20, 0.50, 0.10, 0.05, 0.10, 0.025, 0.025],
        [Reach] = [0.20, 0.10, 0.05, 0.50, 0.10, 0.025, 0.025],
        [Versatility] = [0.50, 0.10, 0.05, 0.20, 0.15, 0.0, 0.0],
        [Portability] = [0.25, 0.10, 0.05, 0.05, 0.05, 0.50, 0.0],
        [Value] = [0.25, 0.10, 0.05, 0.05, 0.05, 0.0, 0.50],
    };

    public static bool IsKnown(string? preset) => preset is not null && Tables.ContainsKey(preset.Trim());

    /// <summary>Raw (un-normalized) weight table of a preset; unknown presets return the balanced table.</summary>
    public static IReadOnlyDictionary<string, double> Get(string? preset)
    {
        var table = Table(preset);
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < table.Length; i++)
        {
            result[CriterionOrder[i]] = table[i];
        }

        return result;
    }

    internal static double[] Table(string? preset) =>
        (preset is not null && Tables.TryGetValue(preset.Trim(), out var t) ? t : Tables[Balanced]).ToArray();

    internal static string Canonical(string? preset) =>
        preset is not null && Tables.ContainsKey(preset.Trim()) ? preset.Trim().ToLowerInvariant() : Balanced;
}
