using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Analysis;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Discovery;
using PhotoClippy.Core.Normalization;
using PhotoClippy.Core.Plugins;
using PhotoClippy.Core.Profiling;
using PhotoClippy.Core.Storage;

namespace PhotoClippy.Core.Pipeline;

public sealed record PipelineServices
{
    public required PluginSet Plugins { get; init; }
    public required AnalysisStore Store { get; init; }
    public required GenreTaxonomy Taxonomy { get; init; }
    public IFrameSource? FrameSource { get; init; }
    public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;
    public string ToolVersion { get; init; } = "0.0.0";
}

/// <summary>
/// discover → metadata (cached) → normalize into captures → frame analysis (cached, parallel) → profile.
/// The result is persisted as a snapshot so recommendations and reports can be regenerated without rescanning.
/// </summary>
public sealed class AnalysisPipeline(AppOptions options, PipelineServices services)
{
    private const int FeatureFlushSize = 500;

    private readonly ILogger _logger = services.LoggerFactory.CreateLogger<AnalysisPipeline>();
    private readonly Dictionary<string, double> _timings = new(StringComparer.Ordinal);
    private readonly List<string> _notes = [];

    public async Task<AnalysisSnapshot> RunAsync(IProgress<PipelineProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var plugins = services.Plugins;
        _notes.AddRange(plugins.Notes);
        if (plugins.Metadata.Count == 0)
        {
            throw new InvalidOperationException("No metadata extractor is available. Check plugins.metadata and `photo-clippy plugins`.");
        }

        if (options.Library.Roots.Count == 0)
        {
            throw new ArgumentException("No library roots given. Pass one or more paths or set library.roots.");
        }

        var files = Time(PipelineStages.Discover, () => Discover(progress, cancellationToken));
        var facts = await TimeAsync(PipelineStages.Metadata, () => ExtractMetadataAsync(files, progress, cancellationToken)).ConfigureAwait(false);

        progress?.Report(new PipelineProgress(PipelineStages.Normalize, 0, 1));
        var captureSet = Time(PipelineStages.Normalize, () => CaptureBuilder.Build(facts));
        facts = null;
        progress?.Report(new PipelineProgress(PipelineStages.Normalize, 1, 1, $"{captureSet.Captures.Count} captures"));

        var (scope, inScope) = ResolveScope(captureSet);
        var builder = new ProfileBuilder(services.Taxonomy, new ProfileBuilderOptions
        {
            HighIsoThreshold = options.Analysis.HighIsoThreshold,
            RecencyHalfLifeYears = options.Analysis.RecencyHalfLifeYears,
            LensSpecs = captureSet.LensSpecs,
            Bodies = captureSet.Bodies,
            TargetBody = options.Recommendation.Body,
        });

        var frameStats = await TimeAsync(PipelineStages.Frames, () => AnalyzeFramesAsync(captureSet, inScope, builder, progress, cancellationToken)).ConfigureAwait(false);

        progress?.Report(new PipelineProgress(PipelineStages.Profile, 0, 1));
        var stopwatch = Stopwatch.StartNew();
        var library = new LibraryInfo
        {
            Roots = [.. options.Library.Roots.Select(r => Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))))],
            FilesSeen = files.Count,
            FilesWithMetadata = captureSet.FilesWithMetadata,
            RawPlusJpeg = captureSet.RawPlusJpeg,
            Scope = scope,
            FileKinds = files.GroupBy(f => f.Kind.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count()),
            TotalBytes = files.Sum(f => f.Size),
            MetadataErrors = captureSet.MetadataErrors,
            DecodeFailed = frameStats.DecodeFailed,
            NotDecodable = frameStats.NotDecodable,
            Notes = _notes,
        };

        var result = builder.Build(library);
        var insights = InsightGenerator.Generate(result.Profile, null, services.Taxonomy);
        _timings[PipelineStages.Profile] = stopwatch.Elapsed.TotalSeconds;
        progress?.Report(new PipelineProgress(PipelineStages.Profile, 1, 1));

        var target = result.Profile.Target;
        var snapshot = new AnalysisSnapshot
        {
            Run = new RunInfo
            {
                RunId = started.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
                StartedUtc = started,
                FinishedUtc = DateTimeOffset.UtcNow,
                ToolVersion = services.ToolVersion,
                Host = Environment.MachineName,
                Plugins = plugins.Manifests,
                Timings = new Dictionary<string, double>(_timings),
                Options = new Dictionary<string, string>
                {
                    ["scope"] = scope,
                    ["frames"] = options.Analysis.Frames ? "on" : "off",
                    ["highIsoThreshold"] = options.Analysis.HighIsoThreshold.ToString(CultureInfo.InvariantCulture),
                    ["roots"] = options.Library.Roots.Count.ToString(CultureInfo.InvariantCulture),
                },
            },
            Profile = result.Profile,
            Samples = result.Samples,
            Systems = result.Systems,
            OwnedByBody = result.OwnedByBody,
            Owned = target?.BodyKey is { } key && result.OwnedByBody.TryGetValue(key, out var owned) ? owned : [],
            Insights = insights,
            Taxonomy = services.Taxonomy,
        };

        await services.Store.SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    private T Time<T>(string stage, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = action();
        _timings[stage] = stopwatch.Elapsed.TotalSeconds;
        return result;
    }

    private async Task<T> TimeAsync<T>(string stage, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await action().ConfigureAwait(false);
        _timings[stage] = stopwatch.Elapsed.TotalSeconds;
        return result;
    }

    private List<SourceFile> Discover(IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        var discovery = new FileDiscovery(new DiscoveryOptions
        {
            Roots = options.Library.Roots,
            Ignore = options.Library.Ignore,
            FollowSymlinks = options.Library.FollowSymlinks,
            IncludeHidden = options.Library.IncludeHidden,
        });

        var files = new List<SourceFile>();
        foreach (var file in discovery.Enumerate(cancellationToken))
        {
            files.Add(file);
            if (files.Count % 500 == 0)
            {
                progress?.Report(new PipelineProgress(PipelineStages.Discover, files.Count, null));
            }
        }

        if (discovery.SkippedDirectories > 0)
        {
            _notes.Add($"{discovery.SkippedDirectories} directories could not be read and were skipped.");
        }

        progress?.Report(new PipelineProgress(PipelineStages.Discover, files.Count, files.Count));
        return files;
    }

    private async Task<List<FileFacts>> ExtractMetadataAsync(List<SourceFile> files, IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        var extractors = services.Plugins.Metadata;
        var extractorKey = string.Join('+', extractors.Select(e => e.Manifest.CacheKey));
        var index = new Dictionary<string, int>(files.Count, StringComparer.Ordinal);
        for (var i = 0; i < files.Count; i++)
        {
            index[files[i].Path] = i;
        }

        var facts = new FileFacts?[files.Count];
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        long cached = 0;
        foreach (var (file, metadata) in services.Store.ReadMetadata(byPath, extractorKey, cancellationToken))
        {
            facts[index[file.Path]] = CaptureBuilder.ToFileFacts(file, metadata);
            cached++;
        }

        byPath.Clear();
        var missing = new List<int>();
        for (var i = 0; i < facts.Length; i++)
        {
            if (facts[i] is null)
            {
                missing.Add(i);
            }
        }

        var total = files.Count;
        long done = cached;
        progress?.Report(new PipelineProgress(PipelineStages.Metadata, done, total, null, cached));

        var batchSize = Math.Max(1, options.Analysis.MetadataBatchSize);
        var batches = missing.Chunk(batchSize).ToList();
        var failures = 0;
        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Analysis.IoConcurrency), CancellationToken = cancellationToken },
            async (batch, ct) =>
            {
                var sources = batch.Select(i => files[i]).ToList();
                var merged = new PhotoMetadata?[sources.Count];
                var anySucceeded = false;
                foreach (var extractor in extractors)
                {
                    var handled = new List<int>();
                    for (var i = 0; i < sources.Count; i++)
                    {
                        if (extractor.CanHandle(sources[i]))
                        {
                            handled.Add(i);
                        }
                    }

                    if (handled.Count == 0)
                    {
                        anySucceeded = true;
                        continue;
                    }

                    try
                    {
                        var results = await extractor.ExtractAsync([.. handled.Select(i => sources[i])], ct).ConfigureAwait(false);
                        if (results.Count != handled.Count)
                        {
                            throw new InvalidOperationException($"{extractor.Manifest.Id} returned {results.Count} results for {handled.Count} files.");
                        }

                        for (var k = 0; k < handled.Count; k++)
                        {
                            var i = handled[k];
                            merged[i] = merged[i] is null ? results[k] : MetadataMerge.Merge(merged[i]!, results[k]);
                        }

                        anySucceeded = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Interlocked.Increment(ref failures);
                        _logger.LogWarning(ex, "Metadata extractor {Id} failed for a batch of {Count} files", extractor.Manifest.Id, handled.Count);
                    }
                }

                var rows = new List<(SourceFile, PhotoMetadata)>(sources.Count);
                for (var i = 0; i < sources.Count; i++)
                {
                    var metadata = merged[i] ?? (anySucceeded ? new PhotoMetadata { Path = sources[i].Path, Warnings = ["No extractor could read this file."] } : null);
                    facts[batch[i]] = CaptureBuilder.ToFileFacts(sources[i], metadata);
                    if (metadata is not null)
                    {
                        rows.Add((sources[i], metadata with { Path = sources[i].Path }));
                    }
                }

                await services.Store.WriteMetadataAsync(rows, extractorKey, ct).ConfigureAwait(false);
                var completed = Interlocked.Add(ref done, batch.Length);
                progress?.Report(new PipelineProgress(PipelineStages.Metadata, completed, total, null, cached));
            }).ConfigureAwait(false);

        if (failures > 0)
        {
            _notes.Add($"Metadata extraction failed for {failures} batch(es); affected files are counted as metadata errors and will be retried next run.");
        }

        return [.. facts.Select((f, i) => f ?? new FileFacts { File = files[i], MetadataError = true })];
    }

    private (string Scope, bool[] InScope) ResolveScope(CaptureSet set)
    {
        var scope = string.Equals(options.Analysis.Scope, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "interchangeable";
        if (scope == "interchangeable" && !set.Captures.Any(c => c.Facts.Device == DeviceClass.InterchangeableLens))
        {
            scope = "all";
            if (set.Captures.Count > 0)
            {
                _notes.Add("No interchangeable-lens captures were found, so statistics include all devices.");
            }
        }

        var since = options.Analysis.Since;
        var until = options.Analysis.Until;
        var flags = new bool[set.Captures.Count];
        for (var i = 0; i < flags.Length; i++)
        {
            var f = set.Captures[i].Facts;
            var deviceOk = scope == "all"
                ? f.Device is not (DeviceClass.Scanner or DeviceClass.Software)
                : f.Device == DeviceClass.InterchangeableLens;
            var dateOk = (since is null && until is null) || (f.CaptureLocal is { } when && (since is null || when >= since) && (until is null || when <= until));
            flags[i] = deviceOk && dateOk;
        }

        if (since is not null || until is not null)
        {
            _notes.Add($"Statistics limited to captures {(since is null ? "up to" : $"from {since:yyyy-MM-dd}")}{(since is not null && until is not null ? " to " : " ")}{(until is null ? string.Empty : $"{until:yyyy-MM-dd}")}.".Replace("  ", " ", StringComparison.Ordinal));
        }

        return (scope, flags);
    }

    private sealed record AnalyzerSlot(int Priority, string Id, string CacheKey, int MaxEdge, ICompositionAnalyzer? Composition, IColorAnalyzer? Color);

    private sealed record WorkItem(int Index, SourceFile File, AnalyzerSlot[] Missing, List<(AnalyzerSlot Slot, object Features)> Cached);

    private sealed record WorkResult(int Index, List<(AnalyzerSlot Slot, object Features)> Parts, List<FeatureRow> Rows, FrameDecodeStatus Status);

    private sealed record FrameStats(int DecodeFailed, int NotDecodable, int Analyzed);

    private async Task<FrameStats> AnalyzeFramesAsync(CaptureSet set, bool[] inScope, ProfileBuilder builder, IProgress<PipelineProgress>? progress, CancellationToken cancellationToken)
    {
        var plugins = services.Plugins;
        var captures = set.Captures;
        var added = new bool[captures.Count];
        var decodeFailed = 0;
        var notDecodable = 0;
        var analyzed = 0;

        void AddPlain(int i, CompositionFeatures? composition = null, ColorFeatures? color = null)
        {
            if (added[i])
            {
                return;
            }

            added[i] = true;
            var assignment = FeatureFusion.AssignGenre(composition?.Genres, services.Taxonomy, options.Analysis.MinGenreConfidence, options.Analysis.JunkThreshold);
            builder.Add(new CaptureAnalysis
            {
                Facts = captures[i].Facts,
                InScope = inScope[i],
                Composition = composition,
                Color = color,
                Genre = assignment.Genre,
                GenreConfidence = assignment.Confidence,
                Junk = inScope[i] && assignment.Junk,
            });
        }

        var slots = new List<AnalyzerSlot>();
        foreach (var c in plugins.Composition)
        {
            slots.Add(new AnalyzerSlot(slots.Count, c.Manifest.Id, c.Manifest.CacheKey, c.Requirements.MaxEdge, c, null));
        }

        foreach (var c in plugins.Color)
        {
            slots.Add(new AnalyzerSlot(slots.Count, c.Manifest.Id, c.Manifest.CacheKey, c.Requirements.MaxEdge, null, c));
        }

        var frameSource = services.FrameSource;
        if (!options.Analysis.Frames || slots.Count == 0 || frameSource is null)
        {
            if (options.Analysis.Frames)
            {
                _notes.Add(frameSource is null ? "No image decoder is available; pixel analysis was skipped." : "No composition or colour analyzers are enabled; pixel analysis was skipped.");
            }
            else
            {
                _notes.Add("Pixel analysis was disabled (--no-frames); subject, composition and colour sections are empty.");
            }

            for (var i = 0; i < captures.Count; i++)
            {
                AddPlain(i);
            }

            return new FrameStats(0, 0, 0);
        }

        var slotById = slots.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var framePathToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var frameFiles = new SourceFile?[captures.Count];
        for (var i = 0; i < captures.Count; i++)
        {
            if (!inScope[i])
            {
                continue;
            }

            var file = captures[i].FrameCandidates.FirstOrDefault(frameSource.CanDecode);
            if (file is null)
            {
                notDecodable++;
                continue;
            }

            frameFiles[i] = file;
            framePathToIndex[file.Path] = i;
        }

        var total = framePathToIndex.Count;
        var cachedParts = new Dictionary<int, List<(AnalyzerSlot Slot, object Features)>>();
        var failed = new HashSet<int>();
        long cachedComplete = 0;

        // Stream cached feature rows (adjacent per path) and finish captures whose analyzers are all cached.
        string? currentPath = null;
        var group = new List<FeatureRow>();
        void FlushGroup()
        {
            if (currentPath is null || !framePathToIndex.TryGetValue(currentPath, out var i))
            {
                return;
            }

            var file = frameFiles[i]!;
            var parts = new List<(AnalyzerSlot Slot, object Features)>();
            foreach (var row in group)
            {
                if (row.Size != file.Size || row.MTime != AnalysisStore.Ticks(file))
                {
                    continue;
                }

                if (row.PluginId == AnalysisStore.FramePluginId)
                {
                    if (!options.Analysis.RetryFailed && row.CacheKey == frameSource.Id)
                    {
                        failed.Add(i);
                    }

                    continue;
                }

                if (slotById.TryGetValue(row.PluginId, out var slot) &&
                    row.CacheKey == CacheKeyFor(slot, captures[i].Facts, file.Path) &&
                    Deserialize(slot, row.Json) is { } features)
                {
                    parts.Add((slot, features));
                }
            }

            if (failed.Contains(i))
            {
                return;
            }

            if (parts.Count == slots.Count)
            {
                Finish(i, parts);
                cachedComplete++;
            }
            else if (parts.Count > 0)
            {
                cachedParts[i] = parts;
            }
        }

        foreach (var row in services.Store.ReadFeatures(cancellationToken))
        {
            if (!string.Equals(row.Path, currentPath, StringComparison.Ordinal))
            {
                FlushGroup();
                group.Clear();
                currentPath = row.Path;
            }

            group.Add(row);
        }

        FlushGroup();
        decodeFailed += failed.Count;
        progress?.Report(new PipelineProgress(PipelineStages.Frames, cachedComplete + failed.Count, total, null, cachedComplete));

        var work = new List<WorkItem>();
        foreach (var (path, i) in framePathToIndex)
        {
            if (added[i] || failed.Contains(i))
            {
                continue;
            }

            var have = cachedParts.GetValueOrDefault(i) ?? [];
            var missing = slots.Where(s => !have.Exists(p => p.Slot == s)).ToArray();
            work.Add(new WorkItem(i, frameFiles[i]!, missing, have));
        }

        // Newest first: if the run is interrupted the most relevant captures are already cached.
        work.Sort((a, b) => Nullable.Compare(captures[b.Index].Facts.CaptureLocal, captures[a.Index].Facts.CaptureLocal));
        cachedParts.Clear();

        var workers = options.Analysis.CpuConcurrency > 0 ? options.Analysis.CpuConcurrency : Environment.ProcessorCount;
        var queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(workers * 2) { SingleWriter = true });
        var results = Channel.CreateBounded<WorkResult>(new BoundedChannelOptions(workers * 4) { SingleReader = true });
        var analyzerErrors = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        long completed = cachedComplete + failed.Count;
        var pendingRows = new List<FeatureRow>();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var item in work)
                {
                    await queue.Writer.WriteAsync(item, token).ConfigureAwait(false);
                }
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }, token);

        var workerTasks = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            await foreach (var item in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var result = await ProcessAsync(item, frameSource, captures[item.Index].Facts, analyzerErrors, token).ConfigureAwait(false);
                await results.Writer.WriteAsync(result, token).ConfigureAwait(false);
            }
        }, token)).ToArray();

        var allWorkers = Task.WhenAll(workerTasks).ContinueWith(t => results.Writer.TryComplete(t.Exception?.GetBaseException()), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            await foreach (var result in results.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                pendingRows.AddRange(result.Rows);
                switch (result.Status)
                {
                    case FrameDecodeStatus.Ok:
                        Finish(result.Index, result.Parts);
                        analyzed++;
                        break;
                    case FrameDecodeStatus.Unsupported:
                        notDecodable++;
                        break;
                    default:
                        decodeFailed++;
                        break;
                }

                completed++;
                progress?.Report(new PipelineProgress(PipelineStages.Frames, completed, total, null, cachedComplete));
                if (pendingRows.Count >= FeatureFlushSize)
                {
                    await services.Store.WriteFeaturesAsync([.. pendingRows], token).ConfigureAwait(false);
                    pendingRows.Clear();
                }
            }

            await producer.ConfigureAwait(false);
            await allWorkers.ConfigureAwait(false);
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            // Persist whatever finished, even on cancellation, so the next run resumes.
            await services.Store.WriteFeaturesAsync([.. pendingRows], CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var (id, count) in analyzerErrors)
        {
            _notes.Add($"{id} failed on {count} image(s); see the log for details.");
        }

        for (var i = 0; i < captures.Count; i++)
        {
            AddPlain(i);
        }

        return new FrameStats(decodeFailed, notDecodable, analyzed);

        void Finish(int i, List<(AnalyzerSlot Slot, object Features)> parts)
        {
            parts.Sort((a, b) => a.Slot.Priority.CompareTo(b.Slot.Priority));
            var compositionParts = parts.Where(p => p.Features is CompositionFeatures).Select(p => (p.Slot.Id, (CompositionFeatures)p.Features)).ToList();
            var colorParts = parts.Where(p => p.Features is ColorFeatures).Select(p => (p.Slot.Id, (ColorFeatures)p.Features)).ToList();
            foreach (var (id, features) in compositionParts)
            {
                if (features.Genres is { Count: > 0 } && plugins.GenreWeights.GetValueOrDefault(id, 1.0) > 0)
                {
                    builder.AddGenreSource(ShortName(id));
                }
            }

            AddPlain(i, FeatureFusion.FuseComposition(compositionParts, plugins.GenreWeights), FeatureFusion.FuseColor(colorParts));
        }
    }

    private static string ShortName(string pluginId)
    {
        foreach (var prefix in new[] { "builtin.composition.", "builtin.", "ext." })
        {
            if (pluginId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return pluginId[prefix.Length..];
            }
        }

        return pluginId;
    }

    private static object? Deserialize(AnalyzerSlot slot, string json)
    {
        try
        {
            return slot.Composition is not null
                ? JsonSerializer.Deserialize<CompositionFeatures>(json, ContractJson.Options)
                : JsonSerializer.Deserialize<ColorFeatures>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<WorkResult> ProcessAsync(WorkItem item, IFrameSource frameSource, CaptureFacts facts, ConcurrentDictionary<string, int> errors, CancellationToken cancellationToken)
    {
        var file = item.File;
        var rows = new List<FeatureRow>();
        var parts = new List<(AnalyzerSlot Slot, object Features)>(item.Cached);
        var maxEdge = item.Missing.Max(s => s.MaxEdge);

        FrameDecodeResult decoded;
        try
        {
            decoded = await Task.Run(() => frameSource.Decode(file, maxEdge, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            decoded = FrameDecodeResult.Failed(ex.Message);
        }

        if (decoded.Status != FrameDecodeStatus.Ok || decoded.Lease is null)
        {
            _logger.LogDebug("Could not decode {Path}: {Error}", file.Path, decoded.Error);
            var status = decoded.Status == FrameDecodeStatus.Ok ? FrameDecodeStatus.Failed : decoded.Status;
            var json = JsonSerializer.Serialize(new { status = status.ToString().ToLowerInvariant(), error = decoded.Error }, ContractJson.Options);
            rows.Add(new FeatureRow(file.Path, AnalysisStore.FramePluginId, frameSource.Id, file.Size, AnalysisStore.Ticks(file), json));
            return new WorkResult(item.Index, parts, rows, status);
        }

        using (var lease = decoded.Lease)
        {
            var context = new AnalysisContext { CaptureId = facts.CaptureId, SourcePath = file.Path, Facts = facts };
            foreach (var slot in item.Missing)
            {
                try
                {
                    object features = slot.Composition is not null
                        ? await slot.Composition.AnalyzeAsync(lease.Frame, context, cancellationToken).ConfigureAwait(false)
                        : await slot.Color!.AnalyzeAsync(lease.Frame, context, cancellationToken).ConfigureAwait(false);
                    parts.Add((slot, features));
                    var json = features is CompositionFeatures c
                        ? JsonSerializer.Serialize(c, ContractJson.Options)
                        : JsonSerializer.Serialize((ColorFeatures)features, ContractJson.Options);
                    rows.Add(new FeatureRow(file.Path, slot.Id, CacheKeyFor(slot, context), file.Size, AnalysisStore.Ticks(file), json));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.AddOrUpdate(slot.Id, 1, (_, n) => n + 1);
                    _logger.LogWarning("{Plugin} failed on {Path}: {Message}", slot.Id, file.Path, ex.Message);
                }
            }
        }

        return new WorkResult(item.Index, parts, rows, FrameDecodeStatus.Ok);
    }

    private static string CacheKeyFor(AnalyzerSlot slot, CaptureFacts facts, string sourcePath) =>
        CacheKeyFor(slot, new AnalysisContext { CaptureId = facts.CaptureId, SourcePath = sourcePath, Facts = facts });

    private static string CacheKeyFor(AnalyzerSlot slot, AnalysisContext context)
    {
        var analyzer = (IPlugin?)slot.Composition ?? slot.Color;
        if (analyzer is not IContextFingerprintProvider fingerprintProvider)
        {
            return slot.CacheKey;
        }

        return $"{slot.CacheKey}#{fingerprintProvider.GetContextFingerprint(context)}";
    }
}
