namespace PhotoClippy.Core;

public static class Stats
{
    public static double Median(List<double> values) => Percentile(values, 0.5);

    /// <summary>Linear-interpolated percentile (p in 0..1). Sorts <paramref name="values"/> in place.</summary>
    public static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        values.Sort();
        return PercentileSorted(values, p);
    }

    public static double PercentileSorted(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var position = Math.Clamp(p, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    /// <summary>Weighted percentile over (value, weight) pairs sorted by value.</summary>
    public static double WeightedPercentile(List<(double Value, double Weight)> items, double p)
    {
        if (items.Count == 0)
        {
            return double.NaN;
        }

        items.Sort((a, b) => a.Value.CompareTo(b.Value));
        var total = 0.0;
        foreach (var item in items)
        {
            total += item.Weight;
        }

        var target = p * total;
        var running = 0.0;
        foreach (var item in items)
        {
            running += item.Weight;
            if (running >= target)
            {
                return item.Value;
            }
        }

        return items[^1].Value;
    }
}
