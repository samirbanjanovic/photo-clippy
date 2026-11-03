using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

public static class FocalSummary
{
    public const string MedianLabel = "Median focal length";
    public const string Explanation = "35 mm-equivalent. Median is the midpoint of focal lengths, not the most-used setting. "
        + "The dominant peak represents the busiest focal-length cluster, which need not contain a majority of photos.";

    public static string Median(ShootingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Signals?.MedianFocal35 is { } median && double.IsFinite(median) && median > 0
            ? Fmt.Focal(median)
            : "–";
    }

    public static (string? Value, string Caption) Dominant(ShootingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var peak = (profile.FocalPeaks ?? [])
            .Where(p => p is not null && double.IsFinite(p.Focal35Mm) && p.Focal35Mm > 0 && p.Share is > 0 and <= 1)
            .OrderByDescending(p => p.Share).ThenBy(p => p.Focal35Mm).FirstOrDefault();
        if (peak is not null)
        {
            var value = Fmt.Focal(peak.Focal35Mm);
            return (value, $"Dominant focal peak: {value} ({Fmt.Pct(peak.Share)} of focal-length samples)");
        }

        if (profile.Distributions?.GetValueOrDefault(DistributionIds.Focal35) is { } distribution)
        {
            var bins = (distribution.Bins ?? []).Where(b => b is not null && double.IsFinite(b.Count) && b.Count > 0).ToArray();
            if (bins.Length > 0)
            {
                var best = bins.OrderByDescending(b => b.Count).First();
                var value = Charts.FocalBinLabel(best);
                var share = best.Count / bins.Sum(b => b.Count);
                return (value, $"Busiest focal range: {value} ({Fmt.Pct(share)} of focal-length samples; peak unavailable)");
            }
        }

        return (null, "Dominant focal peak: unavailable");
    }
}
