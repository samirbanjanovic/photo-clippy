using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Lenses;

/// <summary>
/// Lens catalog backed by the Lensfun database (https://lensfun.github.io, CC BY-SA 3.0). Data is downloaded only by
/// <see cref="RefreshAsync"/> and cached as compact JSON in the plugin's state directory.
/// </summary>
internal sealed partial class LensfunCatalog : ILensCatalog, IDisposable
{
    public const string Attribution = "Lens data: Lensfun database (lensfun.github.io), CC BY-SA 3.0.";
    private const string CacheFile = "lensfun.json";

    /// <summary>Files without interchangeable lenses (fixed-lens compacts, action cams, generic profiles, schema files).</summary>
    private static readonly string[] SkippedPrefixes = ["compact-", "actioncams", "generic", "lensfun-database", "timestamp"];

    /// <summary>Used when the GitHub index is unreachable or rate-limited.</summary>
    private static readonly string[] FallbackFiles =
    [
        "6x6.xml", "contax.xml", "mil-canon.xml", "mil-fujifilm.xml", "mil-hasselblad.xml", "mil-leica.xml", "mil-nikon.xml",
        "mil-olympus.xml", "mil-panasonic.xml", "mil-pentax.xml", "mil-samsung.xml", "mil-samyang.xml", "mil-sigma.xml",
        "mil-sony.xml", "mil-tamron.xml", "mil-tokina.xml", "mil-zeiss.xml", "misc.xml", "om-system.xml", "rf-leica.xml",
        "slr-canon.xml", "slr-hasselblad.xml", "slr-konica-minolta.xml", "slr-leica.xml", "slr-nikon.xml", "slr-olympus.xml",
        "slr-panasonic.xml", "slr-pentax.xml", "slr-ricoh.xml", "slr-samsung.xml", "slr-samyang.xml", "slr-schneider.xml",
        "slr-sigma.xml", "slr-soligor.xml", "slr-sony.xml", "slr-tamron.xml", "slr-tokina.xml", "slr-ussr.xml",
        "slr-vivitar.xml", "slr-zeiss.xml",
    ];

    private readonly LensfunSettings _settings;
    private readonly string _stateDirectory;
    private readonly ILogger _logger;
    private readonly Func<HttpClient>? _httpFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Index? _index;
    private bool _loadAttempted;

    public LensfunCatalog(LensfunSettings settings, string stateDirectory, ILogger logger, Func<HttpClient>? httpFactory = null)
    {
        _settings = settings;
        _stateDirectory = stateDirectory;
        _logger = logger;
        _httpFactory = httpFactory;
        Manifest = new PluginManifest
        {
            Id = LensfunCatalogFactory.PluginId,
            Name = "Lensfun lens database",
            Version = "1.0.0",
            Kind = ComponentKind.LensCatalog,
            Description = "Pick candidate lenses by maker and mount from the open Lensfun database. Downloaded on request (`photo-clippy lenses update`); no weight or price data.",
            Capabilities = ["search", "refresh", "body-mount"],
        };
    }

    public PluginManifest Manifest { get; }

    private string CachePath => Path.Combine(_stateDirectory, CacheFile);

    public async ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        return index is null
            ? PluginHealth.Ok("not downloaded yet: run `photo-clippy lenses update` or use Update on the Lenses page")
            : PluginHealth.Ok(string.Create(CultureInfo.InvariantCulture, $"{index.Data.Lenses.Count} lenses, updated {index.Data.UpdatedUtc:yyyy-MM-dd}"));
    }

    public async Task<LensCatalogInfo> GetInfoAsync(CancellationToken cancellationToken = default) =>
        Describe(await GetIndexAsync(cancellationToken).ConfigureAwait(false));

    public async Task<IReadOnlyList<CatalogLens>> SearchAsync(LensCatalogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            return [];
        }

        var filters = (query.Text ?? string.Empty)
            .Split((char[])[' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Filter.Parse)
            .ToList();
        var limit = Math.Clamp(query.Limit, 1, 1000);
        return
        [
            .. index.Entries
                .Where(e => query.Maker is not { Length: > 0 } maker || string.Equals(e.Lens.Maker, maker, StringComparison.OrdinalIgnoreCase))
                .Where(e => query.Mount is not { Length: > 0 } mount || e.Lens.Mounts.Contains(mount, StringComparer.OrdinalIgnoreCase))
                .Where(e => filters.All(f => f.Matches(e)))
                .Take(limit)
                .Select(e => e.Lens),
        ];
    }

    public async Task<string?> MountForBodyAsync(string? maker, string? model, CancellationToken cancellationToken = default)
    {
        var index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index is null || string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var keys = new[] { Normalize(maker + model), Normalize(model) }.Where(k => k.Length > 0).Distinct().ToList();
        foreach (var key in keys)
        {
            if (index.Bodies.TryGetValue(key, out var mount))
            {
                return mount;
            }
        }

        return null;
    }

    public async Task<LensCatalogInfo> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = string.IsNullOrWhiteSpace(_settings.Directory)
                ? await DownloadAsync(cancellationToken).ConfigureAwait(false)
                : LoadDirectory(_settings.Directory);
            Directory.CreateDirectory(_stateDirectory);
            var temp = CachePath + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, data, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, CachePath, overwrite: true);
            _index = new Index(data);
            _loadAttempted = true;
            _logger.LogInformation("Lens catalog updated: {Count} lenses from {Source}", data.Lenses.Count, data.Source);
            return Describe(_index);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<Index?> GetIndexAsync(CancellationToken cancellationToken)
    {
        if (_index is { } ready || Volatile.Read(ref _loadAttempted))
        {
            return _index;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_loadAttempted)
            {
                _index = LoadCache();
                if (_index is null && !string.IsNullOrWhiteSpace(_settings.Directory) && Directory.Exists(_settings.Directory))
                {
                    // A local folder needs no network, so it's loaded on first use without an explicit refresh.
                    _index = new Index(LoadDirectory(_settings.Directory));
                }

                _loadAttempted = true;
            }

            return _index;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Index? LoadCache()
    {
        if (!File.Exists(CachePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(CachePath);
            var data = JsonSerializer.Deserialize<CatalogData>(stream, ContractJson.Options);
            return data is null ? null : new Index(data);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable lens catalog cache {Path}", CachePath);
            return null;
        }
    }

    private CatalogData LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Lensfun folder not found: {directory}");
        }

        var builder = new Builder();
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml").Where(f => Wanted(Path.GetFileName(f))).Order(StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            builder.Add(stream, Path.GetFileName(file), _logger);
        }

        return builder.Build(Path.GetFullPath(directory));
    }

    private async Task<CatalogData> DownloadAsync(CancellationToken cancellationToken)
    {
        using var http = _httpFactory?.Invoke() ?? CreateHttpClient();
        var files = await ListFilesAsync(http, cancellationToken).ConfigureAwait(false);
        var baseUrl = _settings.BaseUrl.EndsWith('/') ? _settings.BaseUrl : _settings.BaseUrl + "/";
        var downloads = new (string Name, byte[] Data)[files.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
            async (i, ct) => downloads[i] = (files[i], await http.GetByteArrayAsync(new Uri(baseUrl + files[i]), ct).ConfigureAwait(false))).ConfigureAwait(false);

        var builder = new Builder();
        foreach (var (name, data) in downloads)
        {
            using var stream = new MemoryStream(data, writable: false);
            builder.Add(stream, name, _logger);
        }

        return builder.Build(baseUrl);
    }

    private async Task<IReadOnlyList<string>> ListFilesAsync(HttpClient http, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri(_settings.IndexUrl), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var names = document.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("name", out var n) ? n.GetString() : null)
                .OfType<string>()
                .Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && Wanted(n))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (names.Count > 0)
            {
                return names;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Lensfun file index unavailable ({Message}); using the built-in file list", ex.Message);
        }

        return FallbackFiles;
    }

    private HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, _settings.TimeoutSeconds)) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("photo-clippy-lensfun/1.0");
        return http;
    }

    private static bool Wanted(string fileName) =>
        !SkippedPrefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static LensCatalogInfo Describe(Index? index) => index is null
        ? new LensCatalogInfo { Available = false, Attribution = Attribution }
        : new LensCatalogInfo
        {
            Available = true,
            LensCount = index.Data.Lenses.Count,
            Makers = index.Makers,
            Mounts = index.Mounts,
            UpdatedUtc = index.Data.UpdatedUtc,
            Source = index.Data.Source,
            Attribution = Attribution,
        };

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>Cached catalog contents.</summary>
    internal sealed record CatalogData
    {
        public DateTimeOffset UpdatedUtc { get; init; }
        public string? Source { get; init; }
        public List<CatalogLens> Lenses { get; init; } = [];
        /// <summary>Normalized camera name (maker+model and model alone) → mount.</summary>
        public Dictionary<string, string> Bodies { get; init; } = [];
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, CatalogLens> _lenses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _makerSpelling = new(StringComparer.OrdinalIgnoreCase);

        public void Add(Stream xml, string name, ILogger logger)
        {
            List<CatalogLens> lenses;
            List<LensfunCamera> cameras;
            try
            {
                (lenses, cameras) = LensfunXml.Parse(xml);
            }
            catch (System.Xml.XmlException ex)
            {
                logger.LogWarning("Skipping unreadable Lensfun file {File}: {Message}", name, ex.Message);
                return;
            }

            foreach (var lens in lenses)
            {
                // One spelling per maker ("SIGMA" and "Sigma" are the same maker).
                var maker = _makerSpelling.TryGetValue(lens.Maker, out var known) ? known : _makerSpelling[lens.Maker] = lens.Maker;
                var normalized = lens with { Maker = maker };
                _lenses[normalized.Id] = _lenses.TryGetValue(normalized.Id, out var existing)
                    ? existing with { Mounts = [.. existing.Mounts.Union(normalized.Mounts, StringComparer.OrdinalIgnoreCase)] }
                    : normalized;
            }

            foreach (var camera in cameras)
            {
                foreach (var model in camera.Models)
                {
                    _bodies.TryAdd(Normalize(model), camera.Mount);
                    foreach (var maker in camera.Makers)
                    {
                        _bodies.TryAdd(Normalize(maker + model), camera.Mount);
                    }
                }
            }
        }

        public CatalogData Build(string source) => new()
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            Source = source,
            Lenses =
            [
                .. _lenses.Values
                    .OrderBy(l => l.Maker, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(l => l.FocalMinMm).ThenBy(l => l.FocalMaxMm).ThenBy(l => l.MaxApertureWide)
                    .ThenBy(l => l.Model, StringComparer.OrdinalIgnoreCase),
            ],
            Bodies = _bodies,
        };
    }

    private sealed class Index
    {
        public Index(CatalogData data)
        {
            Data = data;
            Entries = [.. data.Lenses.Select(l => new Entry(l))];
            Makers = [.. data.Lenses.Select(l => l.Maker).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            Mounts = [.. data.Lenses.SelectMany(l => l.Mounts).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            Bodies = new Dictionary<string, string>(data.Bodies, StringComparer.Ordinal);
        }

        public CatalogData Data { get; }
        public IReadOnlyList<Entry> Entries { get; }
        public IReadOnlyList<string> Makers { get; }
        public IReadOnlyList<string> Mounts { get; }
        public IReadOnlyDictionary<string, string> Bodies { get; }
    }

    private sealed class Entry(CatalogLens lens)
    {
        public CatalogLens Lens { get; } = lens;
        public string Text { get; } = (lens.DisplayName + " " + string.Join(' ', lens.Mounts) + " " + lens.Type).ToLowerInvariant();
        public string Compact { get; } = Normalize(lens.DisplayName);
    }

    /// <summary>One search word: a focal length (<c>35</c>, <c>35mm</c>), a zoom range (<c>24-70</c>), an aperture (<c>f/1.4</c>) or text.</summary>
    private sealed partial record Filter(string Word, double? Focal, (double Min, double Max)? Range, double? Aperture)
    {
        public static Filter Parse(string word)
        {
            var lower = word.ToLowerInvariant();
            var aperture = ApertureWord().Match(lower);
            if (aperture.Success)
            {
                return new Filter(lower, null, null, Num(aperture.Groups[1].Value));
            }

            var range = RangeWord().Match(lower);
            if (range.Success)
            {
                return new Filter(lower, null, (Num(range.Groups[1].Value), Num(range.Groups[2].Value)), null);
            }

            var focal = FocalWord().Match(lower);
            return focal.Success ? new Filter(lower, Num(focal.Groups[1].Value), null, null) : new Filter(lower, null, null, null);
        }

        public bool Matches(Entry entry)
        {
            var lens = entry.Lens;
            if (Aperture is { } f)
            {
                return lens.MaxApertureWide <= f + 0.05;
            }

            if (Range is { } r)
            {
                return (Math.Abs(lens.FocalMinMm - r.Min) <= 1 && Math.Abs(lens.FocalMaxMm - r.Max) <= 1) || TextMatch(entry);
            }

            if (Focal is { } mm)
            {
                return (mm >= lens.FocalMinMm - 0.5 && mm <= lens.FocalMaxMm + 0.5) || TextMatch(entry);
            }

            return TextMatch(entry);
        }

        private bool TextMatch(Entry entry) =>
            entry.Text.Contains(Word, StringComparison.Ordinal) || (Normalize(Word) is { Length: > 0 } compact && entry.Compact.Contains(compact, StringComparison.Ordinal));

        private static double Num(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        [GeneratedRegex(@"^(?:f/?|1:)(\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant)]
        private static partial Regex ApertureWord();

        [GeneratedRegex(@"^(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)(?:mm)?$", RegexOptions.CultureInvariant)]
        private static partial Regex RangeWord();

        [GeneratedRegex(@"^(\d+(?:\.\d+)?)(?:mm)?$", RegexOptions.CultureInvariant)]
        private static partial Regex FocalWord();
    }
}
