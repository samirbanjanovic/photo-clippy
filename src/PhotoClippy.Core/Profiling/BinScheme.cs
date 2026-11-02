using System.Globalization;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Profiling;

/// <summary>A fixed set of histogram buckets with a value → bucket index mapping.</summary>
public sealed class BinScheme
{
    private readonly Func<double, int> _index;

    private BinScheme(string[] labels, double?[] lower, double?[] upper, Func<double, int> index, AxisScale scale)
    {
        Labels = labels;
        Lower = lower;
        Upper = upper;
        _index = index;
        Scale = scale;
    }

    public string[] Labels { get; }
    public double?[] Lower { get; }
    public double?[] Upper { get; }
    public AxisScale Scale { get; }
    public int Count => Labels.Length;

    public int IndexOf(double value) => Math.Clamp(_index(value), 0, Labels.Length - 1);

    private static string Fmt(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Buckets centred (geometrically) on <paramref name="centers"/>; values outside clamp to the end buckets.</summary>
    public static BinScheme LogCenters(double[] centers, Func<double, string> label)
    {
        var bounds = new double[centers.Length + 1];
        bounds[0] = centers[0] / Math.Sqrt(centers[1] / centers[0]);
        for (var i = 1; i < centers.Length; i++)
        {
            bounds[i] = Math.Sqrt(centers[i - 1] * centers[i]);
        }

        bounds[^1] = centers[^1] * Math.Sqrt(centers[^1] / centers[^2]);
        return new BinScheme(
            [.. centers.Select(label)],
            [.. bounds[..^1].Select(b => (double?)b)],
            [.. bounds[1..].Select(b => (double?)b)],
            value =>
            {
                var i = Array.BinarySearch(bounds, value);
                return i >= 0 ? i : ~i - 1;
            },
            AxisScale.Log);
    }

    /// <summary>Snaps values to the nearest member of a series in log space (aperture, ISO, shutter).</summary>
    public static BinScheme SnapLog(double[] series, Func<double, string> label, AxisScale scale = AxisScale.Ordinal)
    {
        var logs = series.Select(s => Math.Log(s)).ToArray();
        return new BinScheme(
            [.. series.Select(label)],
            [.. series.Select((s, i) => i == 0 ? (double?)null : Math.Sqrt(series[i - 1] * s))],
            [.. series.Select((s, i) => i == series.Length - 1 ? (double?)null : Math.Sqrt(series[i + 1] * s))],
            value =>
            {
                if (value <= 0)
                {
                    return 0;
                }

                var log = Math.Log(value);
                var best = 0;
                var bestDistance = double.MaxValue;
                for (var i = 0; i < logs.Length; i++)
                {
                    var d = Math.Abs(logs[i] - log);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }

                return best;
            },
            scale);
    }

    /// <summary><paramref name="count"/> equal-width buckets over [min, max].</summary>
    public static BinScheme Linear(double min, double max, int count, string format = "0.0#")
    {
        var width = (max - min) / count;
        var labels = new string[count];
        var lower = new double?[count];
        var upper = new double?[count];
        for (var i = 0; i < count; i++)
        {
            var lo = min + (i * width);
            var hi = lo + width;
            lower[i] = lo;
            upper[i] = hi;
            labels[i] = $"{Fmt(lo, format)}–{Fmt(hi, format)}";
        }

        return new BinScheme(labels, lower, upper, value => (int)Math.Floor((value - min) / width), AxisScale.Linear);
    }

    /// <summary>Integer-valued buckets (EV, hour), labels produced by <paramref name="label"/>.</summary>
    public static BinScheme Integers(int min, int max, Func<int, string> label, AxisScale scale = AxisScale.Linear)
    {
        var n = max - min + 1;
        return new BinScheme(
            [.. Enumerable.Range(min, n).Select(label)],
            [.. Enumerable.Range(min, n).Select(v => (double?)(v - 0.5))],
            [.. Enumerable.Range(min, n).Select(v => (double?)(v + 0.5))],
            value => (int)Math.Round(value, MidpointRounding.AwayFromZero) - min,
            scale);
    }

    /// <summary>Thirds of a unit (stops), labels like "0", "⅓", "1⅔", with a final open-ended bucket.</summary>
    public static BinScheme Thirds(double min, double max, bool signed, string? openEndLabel = null)
    {
        var start = (int)Math.Round(min * 3);
        var end = (int)Math.Round(max * 3);
        var labels = new List<string>();
        var lower = new List<double?>();
        var upper = new List<double?>();
        for (var k = start; k <= end; k++)
        {
            labels.Add(k == end && openEndLabel is not null ? openEndLabel : ThirdsLabel(k, signed));
            lower.Add((k - 0.5) / 3.0);
            upper.Add(k == end && openEndLabel is not null ? null : (k + 0.5) / 3.0);
        }

        return new BinScheme([.. labels], [.. lower], [.. upper], value => (int)Math.Round(value * 3, MidpointRounding.AwayFromZero) - start, AxisScale.Linear);
    }

    public static string ThirdsLabel(int thirds, bool signed)
    {
        var sign = thirds < 0 ? "−" : signed && thirds > 0 ? "+" : string.Empty;
        var abs = Math.Abs(thirds);
        var whole = abs / 3;
        var frac = (abs % 3) switch { 1 => "⅓", 2 => "⅔", _ => string.Empty };
        return abs == 0 ? "0" : sign + (whole == 0 ? frac : whole.ToString(CultureInfo.InvariantCulture) + frac);
    }

    public static string FormatShutter(double seconds)
    {
        if (seconds >= 0.95)
        {
            return Fmt(seconds, "0.#") + "s";
        }

        return "1/" + Fmt(Math.Round(1 / seconds), "0");
    }

    public static string FormatFocal(double mm) => Fmt(mm, "0") + "mm";

    public static string FormatAperture(double f) => "f/" + Fmt(f, f < 10 ? "0.0#" : "0");
}

public static class StandardBins
{
    public static readonly double[] FocalCenters = [8, 10, 12, 14, 16, 18, 20, 24, 28, 35, 40, 50, 70, 85, 105, 135, 200, 300, 400, 500, 600, 800];

    public static readonly double[] ApertureSeries =
    [
        0.95, 1.0, 1.1, 1.2, 1.4, 1.6, 1.8, 2.0, 2.2, 2.5, 2.8, 3.2, 3.5, 4.0, 4.5, 5.0, 5.6, 6.3, 7.1, 8, 9, 10, 11, 13, 14, 16, 18, 20, 22, 32,
    ];

    public static readonly double[] IsoSeries = [25, 50, 100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600, 51200, 102400, 204800];

    /// <summary>Full-stop shutter speeds, slow → fast.</summary>
    public static readonly double[] ShutterSeries =
    [
        30, 15, 8, 4, 2, 1, 1 / 2.0, 1 / 4.0, 1 / 8.0, 1 / 15.0, 1 / 30.0, 1 / 60.0, 1 / 125.0, 1 / 250.0, 1 / 500.0, 1 / 1000.0, 1 / 2000.0, 1 / 4000.0, 1 / 8000.0, 1 / 16000.0, 1 / 32000.0,
    ];

    public static BinScheme Focal { get; } = BinScheme.LogCenters(FocalCenters, BinScheme.FormatFocal);
    public static BinScheme Aperture { get; } = BinScheme.SnapLog(ApertureSeries, BinScheme.FormatAperture);
    public static BinScheme Iso { get; } = BinScheme.SnapLog(IsoSeries, v => v.ToString("0", CultureInfo.InvariantCulture));
    public static BinScheme Shutter { get; } = BinScheme.SnapLog(ShutterSeries, BinScheme.FormatShutter);
    public static BinScheme StopsFromMax { get; } = BinScheme.Thirds(0, 4, signed: false, openEndLabel: "4+");
    public static BinScheme Ev100 { get; } = BinScheme.Integers(-6, 18, v => v.ToString(CultureInfo.InvariantCulture));
    public static BinScheme ExposureBias { get; } = BinScheme.Thirds(-3, 3, signed: true);
    public static BinScheme Unit { get; } = BinScheme.Linear(0, 1, 10, "0.0");
    public static BinScheme Signed { get; } = BinScheme.Linear(-1, 1, 10, "0.0");
    public static BinScheme HorizonTilt { get; } = BinScheme.Integers(-5, 5, v => v switch
    {
        -5 => "≤−5°",
        5 => "≥5°",
        < 0 => $"−{-v}°",
        > 0 => $"+{v}°",
        _ => "0°",
    });
    public static BinScheme Hour { get; } = BinScheme.Integers(0, 23, v => v.ToString("00", CultureInfo.InvariantCulture), AxisScale.Ordinal);
}
