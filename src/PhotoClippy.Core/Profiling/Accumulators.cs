using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Profiling;

/// <summary>Weighted counts over a fixed <see cref="BinScheme"/> plus raw values for summary statistics.</summary>
internal sealed class BinnedAccumulator(BinScheme scheme, bool keepValues = true)
{
    private readonly double[] _counts = new double[scheme.Count];
    private readonly List<double> _values = [];

    public BinScheme Scheme => scheme;
    public double Total { get; private set; }

    public void Add(double? value, double weight = 1)
    {
        if (value is not { } v || !double.IsFinite(v))
        {
            return;
        }

        _counts[scheme.IndexOf(v)] += weight;
        Total += weight;
        if (keepValues)
        {
            _values.Add(v);
        }
    }

    /// <summary>Adds a pre-binned vector (e.g. an averaged histogram).</summary>
    public void AddVector(IReadOnlyList<double> shares, double weight = 1)
    {
        var n = Math.Min(shares.Count, _counts.Length);
        for (var i = 0; i < n; i++)
        {
            if (double.IsFinite(shares[i]))
            {
                _counts[i] += shares[i] * weight;
                Total += shares[i] * weight;
            }
        }
    }

    public double[] Counts => _counts;

    public Distribution? Build(string id, string title, string? unit = null, bool trim = true, Func<int, string>? key = null)
    {
        if (Total <= 0)
        {
            return null;
        }

        var first = 0;
        var last = _counts.Length - 1;
        if (trim)
        {
            while (first < last && _counts[first] <= 0)
            {
                first++;
            }

            while (last > first && _counts[last] <= 0)
            {
                last--;
            }
        }

        var bins = new List<Bin>(last - first + 1);
        for (var i = first; i <= last; i++)
        {
            bins.Add(new Bin
            {
                Label = scheme.Labels[i],
                Count = Round(_counts[i]),
                Share = _counts[i] / Total,
                Lower = scheme.Lower[i],
                Upper = scheme.Upper[i],
                Key = key?.Invoke(i),
            });
        }

        return new Distribution
        {
            Id = id,
            Title = title,
            Unit = unit,
            Scale = scheme.Scale,
            Bins = bins,
            Stats = keepValues ? Summary(_values) : null,
        };
    }

    public (int First, int Last) TrimRange()
    {
        var first = 0;
        var last = _counts.Length - 1;
        while (first < last && _counts[first] <= 0)
        {
            first++;
        }

        while (last > first && _counts[last] <= 0)
        {
            last--;
        }

        return (first, last);
    }

    internal static double Round(double value) => Math.Abs(value - Math.Round(value)) < 1e-9 ? Math.Round(value) : Math.Round(value, 3);

    public static SummaryStats? Summary(List<double> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        return new SummaryStats
        {
            Count = values.Count,
            Mean = values.Average(),
            Median = Stats.PercentileSorted(values, 0.5),
            P10 = Stats.PercentileSorted(values, 0.1),
            P90 = Stats.PercentileSorted(values, 0.9),
            Min = values[0],
            Max = values[^1],
        };
    }
}

/// <summary>Weighted counts by category key with display labels.</summary>
internal sealed class CategoryAccumulator
{
    private readonly Dictionary<string, double> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _labels = new(StringComparer.Ordinal);

    public double Total { get; private set; }
    public IReadOnlyDictionary<string, double> Counts => _counts;

    public void Add(string? key, string? label = null, double weight = 1)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        _counts[key] = _counts.GetValueOrDefault(key) + weight;
        Total += weight;
        if (label is not null)
        {
            _labels.TryAdd(key, label);
        }
    }

    public string Label(string key) => _labels.GetValueOrDefault(key, key);

    /// <summary>Keys ordered by <paramref name="order"/> (default: count descending).</summary>
    public IReadOnlyList<string> Keys(IReadOnlyList<string>? order = null) => order is not null
        ? [.. order.Where(_counts.ContainsKey)]
        : [.. _counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key)];

    public Distribution? Build(string id, string title, IReadOnlyList<string>? order = null, int maxBins = int.MaxValue, string otherLabel = "Other", AxisScale scale = AxisScale.Categorical, bool includeEmpty = false)
    {
        if (Total <= 0)
        {
            return null;
        }

        var keys = includeEmpty && order is not null ? order : Keys(order);
        var bins = new List<Bin>();
        double other = 0;
        for (var i = 0; i < keys.Count; i++)
        {
            var count = _counts.GetValueOrDefault(keys[i]);
            if (i < maxBins - 1 || keys.Count <= maxBins)
            {
                bins.Add(new Bin { Label = Label(keys[i]), Key = keys[i], Count = BinnedAccumulator.Round(count), Share = count / Total });
            }
            else
            {
                other += count;
            }
        }

        if (other > 0)
        {
            bins.Add(new Bin { Label = otherLabel, Key = "other", Count = BinnedAccumulator.Round(other), Share = other / Total });
        }

        return new Distribution { Id = id, Title = title, Scale = scale, Bins = bins };
    }
}

/// <summary>Sparse weighted cross-tabulation keyed by (row, column).</summary>
internal sealed class CrossTab
{
    private readonly Dictionary<(string Y, string X), double> _cells = [];
    private readonly Dictionary<string, double> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _cols = new(StringComparer.Ordinal);

    public double Total { get; private set; }

    public void Add(string? y, string? x, double weight = 1)
    {
        if (y is null || x is null)
        {
            return;
        }

        _cells[(y, x)] = _cells.GetValueOrDefault((y, x)) + weight;
        _rows[y] = _rows.GetValueOrDefault(y) + weight;
        _cols[x] = _cols.GetValueOrDefault(x) + weight;
        Total += weight;
    }

    public IReadOnlyList<string> RowsByCount(int max) =>
        [.. _rows.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(max).Select(kv => kv.Key)];

    public IReadOnlyList<string> ColumnsByCount(int max) =>
        [.. _cols.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(max).Select(kv => kv.Key)];

    public bool HasRow(string y) => _rows.ContainsKey(y);
    public bool HasColumn(string x) => _cols.ContainsKey(x);

    public Matrix? Build(string id, string title, string? xTitle, string? yTitle, IReadOnlyList<string> yKeys, IReadOnlyList<string> xKeys, Func<string, string>? yLabel = null, Func<string, string>? xLabel = null)
    {
        if (Total <= 0 || yKeys.Count == 0 || xKeys.Count == 0)
        {
            return null;
        }

        var values = new List<IReadOnlyList<double>>(yKeys.Count);
        foreach (var y in yKeys)
        {
            var row = new double[xKeys.Count];
            for (var x = 0; x < xKeys.Count; x++)
            {
                row[x] = BinnedAccumulator.Round(_cells.GetValueOrDefault((y, xKeys[x])));
            }

            values.Add(row);
        }

        return new Matrix
        {
            Id = id,
            Title = title,
            XTitle = xTitle,
            YTitle = yTitle,
            XLabels = [.. xKeys.Select(k => xLabel?.Invoke(k) ?? k)],
            YLabels = [.. yKeys.Select(k => yLabel?.Invoke(k) ?? k)],
            Values = values,
            Total = BinnedAccumulator.Round(Total),
        };
    }
}

/// <summary>Shares of a boolean condition over the samples where it is defined.</summary>
internal sealed class ShareCounter
{
    public double Hits { get; private set; }
    public double Total { get; private set; }

    public void Add(bool? hit, double weight = 1)
    {
        if (hit is not { } h)
        {
            return;
        }

        Total += weight;
        if (h)
        {
            Hits += weight;
        }
    }

    public double Share => Total > 0 ? Hits / Total : 0;
}
