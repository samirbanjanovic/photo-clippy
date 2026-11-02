using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

/// <summary>What the plugin declared in its <c>initialize</c> response (re-read after every restart).</summary>
internal sealed record PluginDescriptor(
    PluginManifest Manifest,
    int MaxEdge,
    string Format,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<MediaKind> MediaKinds,
    int MaxBatch);

/// <summary>
/// Transport-agnostic plugin connection: initialize handshake and manifest validation, concurrency throttling,
/// frame encoding (temp PPM file for stdio, inline base64 for http) and result decoding.
/// </summary>
internal sealed class ExternalPluginConnection : IAsyncDisposable
{
    public const int DefaultMaxEdge = 512;
    public const int DefaultMaxBatch = 256;
    private const int MinEdge = 32;
    private const int MaxEdgeLimit = 8192;

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly TimeSpan StaleFrameAge = TimeSpan.FromHours(24);

    private readonly IRpcTransport _transport;
    private readonly SemaphoreSlim _throttle;
    private readonly string _tempDirectory;
    private readonly string _safeId;
    private readonly JsonElement _settings;
    private PluginDescriptor _descriptor;
    private long _frameSequence;
    private int _disposed;

    private ExternalPluginConnection(ExternalPluginOptions options, PluginContext context, IRpcTransport transport, ILogger logger, string tempDirectory, string safeId, JsonElement settings, PluginDescriptor descriptor)
    {
        Options = options;
        Taxonomy = context.Taxonomy;
        Logger = logger;
        _transport = transport;
        _tempDirectory = tempDirectory;
        _safeId = safeId;
        _settings = settings;
        _descriptor = descriptor;
        _throttle = new SemaphoreSlim(options.MaxConcurrency, options.MaxConcurrency);
        Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    public ExternalPluginOptions Options { get; }

    public GenreTaxonomy Taxonomy { get; }

    public ILogger Logger { get; }

    public TimeSpan Timeout { get; }

    public PluginDescriptor Descriptor => Volatile.Read(ref _descriptor);

    public bool IsDegraded => _transport.IsDegraded;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public int RestartCount => _transport.RestartCount;

    public int? ProcessId => _transport.ProcessId;

    public static async Task<ExternalPluginConnection> ConnectAsync(ExternalPluginOptions options, PluginContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        Validate(options);

        var logger = context.LoggerFactory.CreateLogger($"PhotoClippy.Plugins.External.{options.Id}");
        var safeId = SanitizeFileName(options.Id);
        var stateDirectory = Path.GetFullPath(Path.Combine(context.StateDirectory, "external", safeId));
        var tempDirectory = Path.GetFullPath(Path.Combine(context.StateDirectory, "external-tmp"));
        Directory.CreateDirectory(stateDirectory);
        Directory.CreateDirectory(tempDirectory);
        DeleteStaleFrames(tempDirectory, logger);

        var settings = options.Settings.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? options.Settings
            : context.Settings.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? context.Settings
            : EmptyObject;
        var initialize = JsonRpc.Params(new InitializeParams
        {
            ContractVersion = ContractInfo.Version,
            Kind = options.Kind,
            PluginId = options.Id,
            Settings = settings,
            DataDirectory = Path.GetFullPath(context.DataDirectory),
            ModelsDirectory = Path.GetFullPath(context.ModelsDirectory),
            StateDirectory = stateDirectory,
            Taxonomy = context.Taxonomy,
        });

        IRpcTransport transport = string.Equals(options.Transport, ExternalPluginOptions.HttpTransport, StringComparison.OrdinalIgnoreCase)
            ? new HttpTransport(options, logger, initialize)
            : new StdioTransport(options, stateDirectory, logger, initialize);
        try
        {
            var result = await transport.StartAsync(cancellationToken).ConfigureAwait(false);
            var descriptor = Describe(options, settings, result);
            var connection = new ExternalPluginConnection(options, context, transport, logger, tempDirectory, safeId, settings, descriptor);
            transport.Reinitialized = connection.OnReinitialized;
            logger.LogInformation(
                "External plugin {PluginId} ready: {Name} {Version} via {Transport} (contract {Contract})",
                options.Id,
                descriptor.Manifest.Name,
                descriptor.Manifest.Version,
                options.Transport,
                descriptor.Manifest.ContractVersion);
            return connection;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, ParamsWriter? writeParams, CancellationToken cancellationToken)
    {
        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _transport.CallAsync(method, writeParams, Timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _throttle.Release();
        }
    }

    /// <summary>Unthrottled call with a custom timeout (health checks must not queue behind slow analyses).</summary>
    public Task<JsonElement> CallDirectAsync(string method, TimeSpan timeout, CancellationToken cancellationToken) =>
        _transport.CallAsync(method, null, timeout, cancellationToken);

    public async Task<JsonElement> AnalyzeFrameAsync(string method, Frame frame, AnalysisContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(context);
        await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? path = null;
        try
        {
            using var ppm = PpmFrame.From(frame, Descriptor.MaxEdge);
            if (_transport.PassesFramesByPath)
            {
                path = Path.Combine(_tempDirectory, string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{_safeId}-{Environment.ProcessId}-{Interlocked.Increment(ref _frameSequence)}.ppm"));
                ppm.WriteFile(path);
            }

            return await _transport.CallAsync(
                method,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("frame"u8);
                    ppm.WriteJson(writer, path);
                    writer.WritePropertyName("context"u8);
                    JsonSerializer.Serialize(writer, context, ContractJson.Options);
                    writer.WriteEndObject();
                },
                Timeout,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (path is not null)
            {
                TryDelete(path);
            }

            _throttle.Release();
        }
    }

    public T Decode<T>(JsonElement result, string method)
    {
        try
        {
            return result.Deserialize<T>(ContractJson.Options)
                ?? throw new JsonException("result is null");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ExternalPluginException(Options.Id, ExternalPluginErrorCodes.InvalidResponse, $"'{method}' returned an unexpected result: {ex.Message}", innerException: ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static void Validate(ExternalPluginOptions options)
    {
        static ArgumentException Invalid(string id, string message) => new($"External plugin '{id}': {message}", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Id))
        {
            throw new ArgumentException("External plugin options need a non-empty 'id'.", nameof(options));
        }

        if (!Enum.IsDefined(options.Kind))
        {
            throw Invalid(options.Id, $"unknown kind '{options.Kind}'.");
        }

        if (string.Equals(options.Transport, ExternalPluginOptions.StdioTransport, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Command))
            {
                throw Invalid(options.Id, "the stdio transport needs a 'command'.");
            }
        }
        else if (string.Equals(options.Transport, ExternalPluginOptions.HttpTransport, StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            {
                throw Invalid(options.Id, "the http transport needs an absolute http(s) 'url'.");
            }
        }
        else
        {
            throw Invalid(options.Id, $"unknown transport '{options.Transport}' (expected 'stdio' or 'http').");
        }

        if (options.TimeoutSeconds <= 0)
        {
            throw Invalid(options.Id, "'timeoutSeconds' must be positive.");
        }

        if (options.MaxConcurrency < 1)
        {
            throw Invalid(options.Id, "'maxConcurrency' must be at least 1.");
        }

        if (options.MaxRestarts < 0)
        {
            throw Invalid(options.Id, "'maxRestarts' must not be negative.");
        }
    }

    internal static PluginDescriptor Describe(ExternalPluginOptions options, JsonElement settings, JsonElement result)
    {
        ExternalPluginException Rejected(string message, Exception? inner = null) =>
            new(options.Id, ExternalPluginErrorCodes.ManifestRejected, message, innerException: inner);

        InitializeResult? init;
        try
        {
            init = result.ValueKind == JsonValueKind.Object ? result.Deserialize<InitializeResult>(ContractJson.Options) : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw Rejected($"invalid initialize result: {ex.Message}", ex);
        }

        var manifest = init?.Manifest ?? throw Rejected("initialize result has no 'manifest'");
        if (manifest.Kind != options.Kind)
        {
            throw Rejected($"plugin declares kind '{ToCamel(manifest.Kind)}' but is configured as '{ToCamel(options.Kind)}'");
        }

        var contractMajor = (manifest.ContractVersion ?? string.Empty).Split('.')[0].Trim();
        var hostMajor = ContractInfo.Version.Split('.')[0];
        if (!string.Equals(contractMajor, hostMajor, StringComparison.Ordinal))
        {
            throw Rejected($"plugin targets contract '{manifest.ContractVersion}', host supports '{ContractInfo.Version}' (major {hostMajor})");
        }

        if (!string.Equals(manifest.Id, options.Id, StringComparison.Ordinal) && !manifest.Id.StartsWith("ext.", StringComparison.Ordinal))
        {
            throw Rejected($"manifest id '{manifest.Id}' must equal the configured id '{options.Id}' or start with 'ext.'");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw Rejected("manifest needs a non-empty 'name' and 'version'");
        }

        if (manifest.Fingerprint is null && settings.ValueKind == JsonValueKind.Object && settings.EnumerateObject().Any())
        {
            manifest = manifest with { Fingerprint = SettingsFingerprint(settings) };
        }

        var maxEdge = Math.Clamp(init.Requirements?.MaxEdge ?? DefaultMaxEdge, MinEdge, MaxEdgeLimit);
        var format = !string.IsNullOrWhiteSpace(init.Format) ? init.Format.Trim()
            : manifest.Capabilities.FirstOrDefault(c => c.StartsWith("format:", StringComparison.OrdinalIgnoreCase))?["format:".Length..]
            ?? manifest.Id[(manifest.Id.LastIndexOf('.') + 1)..];
        var extensions = (init.Extensions ?? [])
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => (e.StartsWith('.') ? e : "." + e).ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var maxBatch = init.MaxBatch is > 0 ? init.MaxBatch.Value : DefaultMaxBatch;
        return new PluginDescriptor(manifest, maxEdge, format, extensions, init.MediaKinds ?? [], maxBatch);
    }

    internal static string SanitizeFileName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Create(id.Length, id, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                span[i] = Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' or ' ' ? '_' : c;
            }
        });
    }

    private void OnReinitialized(JsonElement result)
    {
        try
        {
            var descriptor = Describe(Options, _settings, result);
            if (descriptor.Manifest.CacheKey != Descriptor.Manifest.CacheKey)
            {
                Logger.LogWarning("External plugin {PluginId} changed its manifest on restart ({Old} → {New}); keeping the original", Options.Id, Descriptor.Manifest.CacheKey, descriptor.Manifest.CacheKey);
                return;
            }

            Volatile.Write(ref _descriptor, descriptor);
        }
        catch (ExternalPluginException ex)
        {
            Logger.LogError(ex, "External plugin {PluginId} returned an invalid manifest after restart; keeping the original", Options.Id);
        }
    }

    private static string SettingsFingerprint(JsonElement settings)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(settings.GetRawText()), hash);
        return "s" + Convert.ToHexStringLower(hash[..6]);
    }

    private static string ToCamel(ComponentKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    private static void DeleteStaleFrames(string directory, ILogger logger)
    {
        try
        {
            var cutoff = DateTime.UtcNow - StaleFrameAge;
            foreach (var file in Directory.EnumerateFiles(directory, "*.ppm"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not clean stale frames in {Directory}", directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
