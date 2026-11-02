using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Metadata;

/// <summary>
/// Metadata extractor backed by one long-lived <c>exiftool -stay_open True -@ -</c> process. Each
/// <see cref="ExtractAsync"/> call is sent as a single <c>-execute</c> command; access to the process is serialized.
/// The process is started lazily, restarted when it dies and killed on dispose. Missing exiftool never throws:
/// <see cref="CheckHealthAsync"/> reports it and every result carries a warning.
/// </summary>
public sealed class ExifToolMetadataExtractor : IMetadataExtractor, IAsyncDisposable, IDisposable
{
    public const string PluginId = "builtin.metadata.exiftool";
    private const string MappingVersion = "1";
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);

    // -fast (not -fast2: that skips maker notes and loses LensID). Tags suffixed with '#' are numeric (-n per tag);
    // printed values are kept for names (LensID etc.). Mode tags are restricted to EXIF because maker notes reuse the
    // names with vendor-specific codes. Composite:Lens is synthesized from focal lengths and would shadow the file's
    // own Lens tags (XMP aux:Lens, maker notes), so it is excluded; MapLens also skips a focal-range-only LensID.
    internal static readonly string[] CommonArgs = ["-json", "-fast", "-charset", "filename=utf8"];

    internal static readonly string[] TagArgs =
    [
        "-FileType", "-Make", "-Model", "-SerialNumber", "-Software",
        "-LensMake", "-LensModel", "-LensID", "-Lens", "--Composite:Lens", "-LensInfo#", "-LensSerialNumber",
        "-FocalLength#", "-FocalLengthIn35mmFormat#", "-FNumber#", "-ExposureTime#", "-ApertureValue#", "-ShutterSpeedValue#",
        "-ISO#", "-RecommendedExposureIndex#", "-ISOSpeed#", "-StandardOutputSensitivity#", "-ExposureCompensation#",
        "-EXIF:ExposureProgram#", "-EXIF:MeteringMode#", "-EXIF:WhiteBalance#", "-EXIF:Flash#",
        "-DateTimeOriginal", "-OffsetTimeOriginal", "-SubSecTimeOriginal",
        "-EXIF:CreateDate", "-EXIF:OffsetTimeDigitized", "-EXIF:SubSecTimeDigitized",
        "-EXIF:ModifyDate", "-EXIF:OffsetTime", "-EXIF:SubSecTime",
        "-ImageUniqueID", "-ExifImageWidth#", "-ExifImageHeight#", "-ImageWidth#", "-ImageHeight#", "-Orientation#",
        "-FocalPlaneXResolution#", "-FocalPlaneResolutionUnit#", "-SubjectDistance#", "-Rating#",
        "-GPSLatitude#", "-GPSLongitude#", "-GPSLatitudeRef#", "-GPSLongitudeRef#",
        "-Warning", "-Error",
    ];

    private readonly ExifToolSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SidecarLocator _sidecars = new();
    private ExifToolProcess? _process;
    private int _sequence;
    private int _disposed;

    public ExifToolMetadataExtractor()
        : this(new ExifToolSettings())
    {
    }

    public ExifToolMetadataExtractor(ExifToolSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "ExifTool metadata",
            Version = "1.0.0",
            Kind = ComponentKind.Metadata,
            Description = "Reads metadata with a persistent exiftool -stay_open process (" + settings.ExecutablePath + ").",
            Capabilities = ["exif", "xmp", "xmp-sidecar", "makernote-lens", "lens-id", "gps", "jpeg", "heif", "tiff", "png", "webp", "raw"],
            Fingerprint = NativeMetadataExtractor.ShortHash(
                "exiftool|" + MappingVersion + "|" + string.Join(' ', CommonArgs) + "|" + string.Join(' ', TagArgs)),
        };
    }

    public PluginManifest Manifest { get; }

    /// <summary>OS process id of the current exiftool process (tests use it to simulate a crash).</summary>
    internal int? CurrentProcessId => Volatile.Read(ref _process)?.ProcessId;

    internal TimeSpan Timeout => TimeSpan.FromSeconds(_settings.TimeoutSeconds is > 0 and <= 86_400 ? _settings.TimeoutSeconds : 120);

    public bool CanHandle(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return SupportedExtensions.ExifTool.Contains(file.Extension);
    }

    public async ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return PluginHealth.Unhealthy("exiftool extractor has been disposed");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var output = await ExecuteAsync(["-ver"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            var version = output.Stdout.Trim();
            return version.Length > 0 && char.IsAsciiDigit(version[0])
                ? PluginHealth.Ok("exiftool " + version)
                : PluginHealth.Unhealthy(NotFound("unexpected -ver output '" + Truncate(version, 80) + "'"));
        }
        catch (ExifToolUnavailableException ex)
        {
            return PluginHealth.Unhealthy(NotFound(ex.Message));
        }
        catch (TimeoutException ex)
        {
            return PluginHealth.Unhealthy(NotFound(ex.Message));
        }
        catch (IOException ex)
        {
            return PluginHealth.Unhealthy(NotFound(ex.Message));
        }
        catch (ObjectDisposedException)
        {
            return PluginHealth.Unhealthy("exiftool extractor has been disposed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PhotoMetadata>> ExtractAsync(IReadOnlyList<SourceFile> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (batch.Count == 0)
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        var results = new PhotoMetadata?[batch.Count];
        var requests = new List<Request>(batch.Count);
        var args = new List<string>(CommonArgs.Length + TagArgs.Length + batch.Count);
        args.AddRange(CommonArgs);
        args.AddRange(TagArgs);
        for (var i = 0; i < batch.Count; i++)
        {
            var path = batch[i].Path;
            if (ToArgument(path) is not { } argument)
            {
                results[i] = MetadataDraft.Failed(path ?? string.Empty, PluginId, "path cannot be passed to exiftool");
                continue;
            }

            args.Add(argument);
            requests.Add(new Request(i, path, argument));
        }

        if (requests.Count > 0)
        {
            ExifToolOutput? output = null;
            string? failure = null;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                output = await ExecuteWithRestartAsync(args, cancellationToken).ConfigureAwait(false);
            }
            catch (ExifToolUnavailableException ex)
            {
                failure = "exiftool unavailable: " + ex.Message;
            }
            catch (TimeoutException ex)
            {
                failure = ex.Message;
            }
            catch (IOException ex)
            {
                failure = "exiftool failed: " + ex.Message;
            }
            finally
            {
                _gate.Release();
            }

            if (output is null)
            {
                foreach (var r in requests)
                {
                    results[r.Index] = MetadataDraft.Failed(r.Path, PluginId, failure ?? "exiftool failed");
                }
            }
            else
            {
                MapOutput(output, requests, results, cancellationToken);
            }
        }

        return Array.ConvertAll(results, r => r!);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        if (_gate.Wait(0))
        {
            try
            {
                process.Shutdown(ShutdownWait);
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        if (_gate.Wait(0))
        {
            try
            {
                await process.ShutdownAsync(ShutdownWait).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            process.Dispose();
        }
    }

    private string NotFound(string reason) =>
        "exiftool not found or not working ('" + _settings.ExecutablePath + "'): " + reason;

    private static string? ToArgument(string? path)
    {
        // The -@ argfile is line based and trims whitespace; '#' starts a comment. Full paths avoid both issues.
        if (string.IsNullOrWhiteSpace(path) || path.AsSpan().ContainsAny('\r', '\n'))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return full.Length == full.Trim().Length ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private async Task<ExifToolOutput> ExecuteWithRestartAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            return await ExecuteAsync(args, Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The process died (crash, killed externally): start a fresh one and retry the batch once.
            return await ExecuteAsync(args, Timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one command. Caller must hold <see cref="_gate"/>.</summary>
    private async Task<ExifToolOutput> ExecuteAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var process = EnsureProcess();
        var sequence = ++_sequence;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await process.RunAsync(args, sequence, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DropProcess(process);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("exiftool did not finish within " + timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s; process restarted");
        }
        catch (IOException)
        {
            DropProcess(process);
            throw;
        }
    }

    private ExifToolProcess EnsureProcess()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var process = _process;
        if (process is not null && process.IsAlive)
        {
            return process;
        }

        if (process is not null)
        {
            DropProcess(process);
        }

        process = ExifToolProcess.Start(_settings.ExecutablePath);
        Volatile.Write(ref _process, process);
        if (Volatile.Read(ref _disposed) != 0)
        {
            DropProcess(process);
            throw new ObjectDisposedException(GetType().FullName);
        }

        return process;
    }

    private void DropProcess(ExifToolProcess process)
    {
        Interlocked.CompareExchange(ref _process, null, process);
        process.Dispose();
    }

    private void MapOutput(ExifToolOutput output, List<Request> requests, PhotoMetadata?[] results, CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, List<JsonElement>>(PathComparer);
        JsonDocument? document = null;
        string? parseError = null;
        var json = output.Stdout.AsSpan().Trim();
        if (json.Length > 0)
        {
            try
            {
                document = JsonDocument.Parse(output.Stdout);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in document.RootElement.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.Object
                            && element.TryGetProperty("SourceFile", out var source)
                            && source.GetString() is { } sourceFile)
                        {
                            var key = NormalizePath(sourceFile);
                            if (!entries.TryGetValue(key, out var list))
                            {
                                entries[key] = list = new List<JsonElement>(1);
                            }

                            list.Add(element);
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                parseError = "exiftool returned invalid JSON: " + ex.Message;
            }
        }

        using (document)
        {
            var used = new Dictionary<string, int>(PathComparer);
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = NormalizePath(request.Argument);
                if (entries.TryGetValue(key, out var list))
                {
                    used.TryGetValue(key, out var n);
                    used[key] = n + 1;
                    var element = list[Math.Min(n, list.Count - 1)];
                    results[request.Index] = MapEntry(element, request.Path);
                }
                else
                {
                    results[request.Index] = MetadataDraft.Failed(request.Path, PluginId,
                        parseError ?? ErrorFor(output.Stderr, request.Argument) ?? "exiftool returned no data");
                }
            }
        }
    }

    private static string? ErrorFor(IReadOnlyList<string> stderr, string argument)
    {
        foreach (var line in stderr)
        {
            var at = line.IndexOf(argument, StringComparison.Ordinal);
            if (at >= 0)
            {
                var message = line[..at].TrimEnd(' ', '-', ':').Trim();
                return "exiftool: " + (message.Length > 0 ? message : line.Trim());
            }
        }

        return null;
    }

    private PhotoMetadata MapEntry(JsonElement e, string path)
    {
        var d = new MetadataDraft();
        try
        {
            Map(e, d);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            d.Warn("metadata mapping failed: " + ex.Message);
        }

        d.ApplySidecar(_sidecars, path);
        return d.Build(path, PluginId);
    }

    internal static void Map(JsonElement e, MetadataDraft d)
    {
        d.Make = ExifMapping.Clean(Str(e, "Make"));
        d.Model = ExifMapping.Clean(Str(e, "Model"));
        d.Software = ExifMapping.Clean(Str(e, "Software"));
        d.BodySerial = ExifMapping.CleanSerial(Str(e, "SerialNumber"));
        d.LensMake = ExifMapping.Clean(Str(e, "LensMake"));
        d.LensSerial = ExifMapping.CleanSerial(Str(e, "LensSerialNumber"));
        d.LensSpec = ExifMapping.ParseLensInfo(Str(e, "LensInfo"));
        MapLens(e, d);

        d.FocalLengthMm = ExifMapping.Positive(Num(e, "FocalLength"));
        d.FocalLength35mm = ExifMapping.Positive(Num(e, "FocalLengthIn35mmFormat"));
        // ExifTool's numeric ApertureValue/ShutterSpeedValue are already converted from APEX to f-number/seconds;
        // they are mapped back to APEX so the rounding matches the native extractor.
        d.FNumber = ExifMapping.Positive(Num(e, "FNumber"))
                    ?? (ExifMapping.Positive(Num(e, "ApertureValue")) is { } av ? ExifMapping.FNumberFromApex(2 * Math.Log2(av)) : null);
        d.ExposureTimeS = ExifMapping.Positive(Num(e, "ExposureTime"))
                          ?? (ExifMapping.Positive(Num(e, "ShutterSpeedValue")) is { } tv ? ExifMapping.ExposureFromApex(-Math.Log2(tv)) : null);
        d.ExposureBiasEv = Num(e, "ExposureCompensation") is { } bias && double.IsFinite(bias) && Math.Abs(bias) <= 20 ? bias : null;
        MapIso(e, d);

        if (Num(e, "ExposureProgram") is { } program)
        {
            d.ExposureProgram = ExifMapping.ExposureProgramName((int)program);
        }

        if (Num(e, "MeteringMode") is { } metering)
        {
            d.MeteringMode = ExifMapping.MeteringModeName((int)metering);
        }

        if (Num(e, "WhiteBalance") is { } wb)
        {
            d.WhiteBalance = ExifMapping.WhiteBalanceName((int)wb);
        }

        if (Num(e, "Flash") is { } flash && flash >= 0)
        {
            d.FlashFired = ((int)flash & 1) == 1;
        }

        MapDates(e, d);
        d.ImageUniqueId = ExifMapping.CleanSerial(Str(e, "ImageUniqueID"));
        d.Orientation = ExifMapping.ValidOrientation(Num(e, "Orientation"));
        MapDimensions(e, d);

        d.FocalPlaneXResolutionPerMm = ExifMapping.FocalPlanePerMm(
            Num(e, "FocalPlaneXResolution"),
            Num(e, "FocalPlaneResolutionUnit") is { } unit ? (int)unit : null);
        d.SubjectDistanceM = ExifMapping.SubjectDistance(Num(e, "SubjectDistance"));
        d.Rating = ExifMapping.ValidRating(Num(e, "Rating"));

        if (Num(e, "GPSLatitude") is { } lat && Num(e, "GPSLongitude") is { } lon)
        {
            if (lat > 0 && Str(e, "GPSLatitudeRef") is { } latRef && latRef.StartsWith('S'))
            {
                lat = -lat;
            }

            if (lon > 0 && Str(e, "GPSLongitudeRef") is { } lonRef && lonRef.StartsWith('W'))
            {
                lon = -lon;
            }

            if (ExifMapping.ValidGps(lat, lon))
            {
                d.HasGps = true;
                d.Latitude = lat;
                d.Longitude = lon;
            }
        }

        if (ExifMapping.Clean(Str(e, "FileType")) is { } fileType)
        {
            d.SetExtra("fileType", fileType);
        }

        if (ExifMapping.Clean(Str(e, "Error")) is { } error)
        {
            d.Warn("exiftool: " + error);
        }

        if (ExifMapping.Clean(Str(e, "Warning")) is { } warning)
        {
            d.Warn("exiftool: " + warning);
        }
    }

    private static void MapLens(JsonElement e, MetadataDraft d)
    {
        d.LensModel = ExifMapping.UsefulLensName(Str(e, "LensModel"));
        if (d.LensModel is not null)
        {
            return;
        }

        foreach (var (key, source) in (ReadOnlySpan<(string, string)>)[("LensID", "lensId"), ("Lens", "lens")])
        {
            var value = ExifMapping.Clean(Str(e, key));
            if (value is null)
            {
                continue;
            }

            // Composite:LensID degrades to a bare focal range when it cannot decode a lens type; that is not a name.
            // A recorded Lens tag (XMP aux:Lens, maker note) is kept verbatim, like the native extractor does.
            if (key == "LensID" && ExifMapping.IsFocalRangeOnly(value))
            {
                continue;
            }

            if (value.Contains(" or ", StringComparison.Ordinal))
            {
                // ExifTool could not disambiguate the lens type; never pick one of the candidates.
                d.SetExtra("lensCandidates", value);
                continue;
            }

            if (ExifMapping.UsefulLensName(value) is { } name)
            {
                d.LensModel = name;
                d.SetExtra("lensSource", source);
                return;
            }
        }
    }

    private static void MapIso(JsonElement e, MetadataDraft d)
    {
        var raw = Num(e, "ISO");
        if (raw is { } r && r > 0 && r < 65535)
        {
            d.Iso = ExifMapping.ValidIso(r);
            return;
        }

        foreach (var (key, name) in (ReadOnlySpan<(string, string)>)
                 [
                     ("RecommendedExposureIndex", "recommendedExposureIndex"),
                     ("ISOSpeed", "isoSpeed"),
                     ("StandardOutputSensitivity", "standardOutputSensitivity"),
                 ])
        {
            if (ExifMapping.ValidIso(Num(e, key)) is { } iso)
            {
                d.Iso = iso;
                d.SetExtra("isoSource", name);
                return;
            }
        }

        if (raw is >= 65535)
        {
            d.Iso = 65535;
            d.SetExtra("isoSource", "saturated");
        }
    }

    private static void MapDates(JsonElement e, MetadataDraft d)
    {
        foreach (var (dateKey, offsetKey, subSecKey, source) in (ReadOnlySpan<(string, string, string, string?)>)
                 [
                     ("DateTimeOriginal", "OffsetTimeOriginal", "SubSecTimeOriginal", null),
                     ("CreateDate", "OffsetTimeDigitized", "SubSecTimeDigitized", "createDate"),
                     ("ModifyDate", "OffsetTime", "SubSecTime", "dateTime"),
                 ])
        {
            if (ExifMapping.ParseDateTime(Str(e, dateKey)) is { } date)
            {
                d.CaptureLocal = date;
                d.OffsetTime = ExifMapping.ParseOffset(Str(e, offsetKey));
                d.SubSec = ExifMapping.ParseSubSec(Str(e, subSecKey));
                if (source is not null)
                {
                    d.SetExtra("captureSource", source);
                }

                return;
            }
        }
    }

    private static void MapDimensions(JsonElement e, MetadataDraft d)
    {
        foreach (var (widthKey, heightKey) in (ReadOnlySpan<(string, string)>)[("ExifImageWidth", "ExifImageHeight"), ("ImageWidth", "ImageHeight")])
        {
            if (ExifMapping.ValidDimension(Num(e, widthKey)) is { } w && ExifMapping.ValidDimension(Num(e, heightKey)) is { } h)
            {
                (d.Width, d.Height) = (w, h);
                return;
            }
        }
    }

    internal static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => value.GetArrayLength() > 0 ? StrOf(value[0]) : null,
            _ => null,
        };
    }

    private static string? StrOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    internal static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() == 0)
            {
                return null;
            }

            value = value[0];
        }

        double? result = value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var v) ? v : null,
            JsonValueKind.String => ExifMapping.ParseNumber(value.GetString()),
            _ => null,
        };
        return result is { } r && double.IsFinite(r) ? r : null;
    }

    private static string NormalizePath(string path) =>
        OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed record Request(int Index, string Path, string Argument);
}

internal sealed record ExifToolOutput(string Stdout, IReadOnlyList<string> Stderr);

internal sealed class ExifToolUnavailableException : Exception
{
    public ExifToolUnavailableException()
    {
    }

    public ExifToolUnavailableException(string message)
        : base(message)
    {
    }

    public ExifToolUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One <c>exiftool -stay_open True -@ -</c> child process. Not thread-safe: callers serialize commands.</summary>
internal sealed class ExifToolProcess : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private const int MaxStderrLines = 10_000;

    private readonly Process _process;
    private readonly Lock _stderrLock = new();
    private readonly List<string> _stderr = [];
    private readonly Task _stderrPump;
    private string? _stderrMarker;
    private TaskCompletionSource<string[]>? _stderrDone;
    private bool _stderrClosed;
    private int _disposed;

    private ExifToolProcess(Process process)
    {
        _process = process;
        _stderrPump = Task.Run(PumpStderrAsync);
    }

    public int ProcessId => _process.Id;

    public bool IsAlive
    {
        get
        {
            try
            {
                return Volatile.Read(ref _disposed) == 0 && !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static ExifToolProcess Start(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ExifToolUnavailableException("no executable path configured");
        }

        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };

        if (executablePath.EndsWith(".pl", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = "perl";
            info.ArgumentList.Add(executablePath);
        }
        else
        {
            info.FileName = executablePath;
        }

        foreach (var arg in (ReadOnlySpan<string>)["-stay_open", "True", "-@", "-"])
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            var process = Process.Start(info) ?? throw new ExifToolUnavailableException("process could not be started");
            return new ExifToolProcess(process);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ExifToolUnavailableException(ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ExifToolUnavailableException(ex.Message, ex);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new ExifToolUnavailableException(ex.Message, ex);
        }
    }

    /// <summary>Sends <paramref name="args"/> as command <paramref name="sequence"/> and returns its output.</summary>
    /// <exception cref="IOException">The process exited or closed its pipes.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the process has been killed.</exception>
    public async Task<ExifToolOutput> RunAsync(IReadOnlyList<string> args, int sequence, CancellationToken cancellationToken)
    {
        var readyMarker = "{ready" + sequence.ToString(CultureInfo.InvariantCulture) + "}";
        var stderrMarker = "{stderr" + sequence.ToString(CultureInfo.InvariantCulture) + "}";
        var stderrDone = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stderrLock)
        {
            _stderr.Clear();
            _stderrMarker = stderrMarker;
            _stderrDone = stderrDone;
            if (_stderrClosed)
            {
                stderrDone.TrySetResult([]);
            }
        }

        await using var registration = cancellationToken.Register(static s => ((ExifToolProcess)s!).Kill(), this).ConfigureAwait(false);
        try
        {
            var command = new StringBuilder(64 * (args.Count + 4));
            foreach (var arg in args)
            {
                command.Append(arg).Append('\n');
            }

            command.Append("-echo4\n").Append(stderrMarker).Append('\n')
                .Append("-execute").Append(sequence.ToString(CultureInfo.InvariantCulture)).Append('\n');

            var stdin = _process.StandardInput;
            await stdin.WriteAsync(command, cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

            var stdout = new StringBuilder(4096);
            var reader = _process.StandardOutput;
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new IOException(await DescribeExitAsync().ConfigureAwait(false));
                }

                if (line == readyMarker)
                {
                    break;
                }

                stdout.Append(line).Append('\n');
            }

            string[] stderr;
            try
            {
                stderr = await stderrDone.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                lock (_stderrLock)
                {
                    stderr = [.. _stderr];
                }
            }

            return new ExifToolOutput(stdout.ToString(), stderr);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ex is IOException)
            {
                throw;
            }

            throw new IOException("exiftool pipe closed: " + ex.Message, ex);
        }
    }

    public void Shutdown(TimeSpan wait)
    {
        try
        {
            if (IsAlive)
            {
                _process.StandardInput.Write("-stay_open\nFalse\n");
                _process.StandardInput.Flush();
                _process.WaitForExit(wait);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        Dispose();
    }

    public async Task ShutdownAsync(TimeSpan wait)
    {
        try
        {
            if (IsAlive)
            {
                await _process.StandardInput.WriteAsync("-stay_open\nFalse\n").ConfigureAwait(false);
                await _process.StandardInput.FlushAsync().ConfigureAwait(false);
                using var cts = new CancellationTokenSource(wait);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
        }

        Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Kill();
        try
        {
            _process.WaitForExit(1000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        _process.Dispose();
    }

    private void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private async Task<string> DescribeExitAsync()
    {
        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        string tail;
        lock (_stderrLock)
        {
            tail = string.Join(" | ", _stderr.TakeLast(3)).Trim();
        }

        string code;
        try
        {
            code = _process.WaitForExit(1000) ? "exit code " + _process.ExitCode.ToString(CultureInfo.InvariantCulture) : "still running";
        }
        catch (InvalidOperationException)
        {
            code = "unknown exit code";
        }

        return "exiftool exited unexpectedly (" + code + ")" + (tail.Length > 0 ? ": " + tail : string.Empty);
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            var reader = _process.StandardError;
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrLock)
                {
                    if (_stderrMarker is not null && line == _stderrMarker)
                    {
                        _stderrDone?.TrySetResult([.. _stderr]);
                        _stderr.Clear();
                        _stderrMarker = null;
                    }
                    else if (_stderr.Count < MaxStderrLines)
                    {
                        _stderr.Add(line);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        lock (_stderrLock)
        {
            _stderrClosed = true;
            _stderrDone?.TrySetResult([.. _stderr]);
        }
    }
}
