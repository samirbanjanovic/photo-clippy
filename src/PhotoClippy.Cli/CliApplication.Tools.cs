using System.CommandLine;
using System.Runtime.InteropServices;
using System.Text.Json;
using PhotoClippy.Contracts;
using PhotoClippy.Core.Analysis;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Core.Discovery;
using PhotoClippy.Core.Normalization;
using PhotoClippy.Core.Pipeline;
using PhotoClippy.Core.Services;
using Spectre.Console;

namespace PhotoClippy.Cli;

internal static partial class CliApplication
{
    private static Command BuildInspectCommand()
    {
        var command = new Command("inspect", "Debug one photo: raw metadata per extractor, the normalized capture, the decoded frame and every analyzer's output.");
        var file = new Argument<string>("file") { Description = "Image file." };
        command.Arguments.Add(file);
        command.Options.Add(NoFramesOption);
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var path = Path.GetFullPath(parse.GetValue(file)!);
            if (!File.Exists(path))
            {
                throw new CliException(ExitCodes.Usage, $"File not found: {path}");
            }

            if (!MediaTypes.TryGetKind(Path.GetFileName(path.AsSpan()), out var kind))
            {
                throw new CliException(ExitCodes.Usage, $"Unsupported file type '{Path.GetExtension(path)}'.", $"Supported: {string.Join(" ", MediaTypes.Extensions.Order())}");
            }

            var info = new FileInfo(path);
            var source = new SourceFile { Path = path, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Kind = kind };
            var kinds = new HashSet<ComponentKind> { ComponentKind.Metadata };
            if (runtime.Options.Analysis.Frames)
            {
                kinds.Add(ComponentKind.Composition);
                kinds.Add(ComponentKind.Color);
            }

            await using var plugins = await runtime.CreatePluginsAsync(kinds, cancellationToken: token).ConfigureAwait(false);
            Presenter.Notes(ui, plugins.Notes);

            var extracted = new List<object>();
            PhotoMetadata? merged = null;
            foreach (var extractor in plugins.Metadata)
            {
                var metadata = (await extractor.ExtractAsync([source], token).ConfigureAwait(false)).FirstOrDefault();
                extracted.Add(new { plugin = extractor.Manifest.Id, metadata });
                if (metadata is not null)
                {
                    Properties(ui, $"Metadata · {extractor.Manifest.Id}", metadata);
                    merged = merged is null ? metadata : MetadataMerge.Merge(merged, metadata);
                }
            }

            var facts = CaptureBuilder.Build([CaptureBuilder.ToFileFacts(source, merged)]).Captures.FirstOrDefault()?.Facts;
            if (facts is not null)
            {
                Properties(ui, "Normalized capture", facts);
            }

            object? frame = null;
            var analyzers = new List<object>();
            CompositionFeatures? composition = null;
            ColorFeatures? color = null;
            GenreAssignment? genre = null;
            var frameSource = runtime.CreateFrameSource();
            if (frameSource is not null && (plugins.Composition.Count > 0 || plugins.Color.Count > 0))
            {
                var maxEdge = plugins.Composition.Select(p => p.Requirements.MaxEdge).Concat(plugins.Color.Select(p => p.Requirements.MaxEdge)).Max();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var decoded = frameSource.CanDecode(source) ? frameSource.Decode(source, maxEdge, token) : FrameDecodeResult.Unsupported("no decoder for this file type");
                var decodeMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (decoded.Lease is { } lease)
                {
                    using (lease)
                    {
                        frame = new { status = "ok", method = lease.Method, lease.Frame.Width, lease.Frame.Height, lease.Frame.SourceWidth, lease.Frame.SourceHeight, ms = Math.Round(decodeMs, 1) };
                        ui.Info($"Decoded {lease.Frame.SourceWidth}×{lease.Frame.SourceHeight} → {lease.Frame.Width}×{lease.Frame.Height} via {lease.Method ?? frameSource.Id} in {decodeMs:0} ms");
                        var context = new AnalysisContext { CaptureId = facts?.CaptureId ?? path, SourcePath = path, Facts = facts };
                        var compositionParts = new List<(string, CompositionFeatures)>();
                        var colorParts = new List<(string, ColorFeatures)>();
                        foreach (var analyzer in plugins.Composition)
                        {
                            var features = await analyzer.AnalyzeAsync(lease.Frame, context, token).ConfigureAwait(false);
                            compositionParts.Add((analyzer.Manifest.Id, features));
                            analyzers.Add(new { plugin = analyzer.Manifest.Id, composition = features });
                            Properties(ui, $"Composition · {analyzer.Manifest.Id}", features);
                        }

                        foreach (var analyzer in plugins.Color)
                        {
                            var features = await analyzer.AnalyzeAsync(lease.Frame, context, token).ConfigureAwait(false);
                            colorParts.Add((analyzer.Manifest.Id, features));
                            analyzers.Add(new { plugin = analyzer.Manifest.Id, color = features });
                            Properties(ui, $"Color · {analyzer.Manifest.Id}", features);
                        }

                        composition = FeatureFusion.FuseComposition(compositionParts, plugins.GenreWeights);
                        color = FeatureFusion.FuseColor(colorParts);
                        genre = FeatureFusion.AssignGenre(composition?.Genres, runtime.Taxonomy, runtime.Options.Analysis.MinGenreConfidence, runtime.Options.Analysis.JunkThreshold);
                        Genres(ui, runtime, genre);
                    }
                }
                else
                {
                    frame = new { status = decoded.Status.ToString().ToLowerInvariant(), error = decoded.Error };
                    ui.Warn($"Frame not decoded ({decoded.Status}): {decoded.Error}");
                }
            }

            ui.Emit("inspect", new { file = source, extractors = extracted, facts, frame, analyzers, fused = new { composition, color, genre } });
            return ExitCodes.Ok;
        }, options =>
        {
            if (parse.GetValue(NoFramesOption))
            {
                options.Analysis.Frames = false;
            }
        }));
        return command;
    }

    private static Command BuildPluginsCommand()
    {
        var command = new Command("plugins", "List built-in, directory and external plugins with version, selection and health.");
        var noHealth = new Option<bool>("--no-health") { Description = "Skip health checks (faster; does not start external processes' checks)." };
        command.Options.Add(noHealth);
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var (rows, notes) = await PluginInventory.CollectAsync(runtime, !parse.GetValue(noHealth), token).ConfigureAwait(false);
            if (ui.ShowHuman)
            {
                // Grouped list rather than a table: ids, versions and long health messages stay readable at 80 columns.
                var slate = Ui.Slate.ToMarkup();
                foreach (var group in rows.GroupBy(r => r.Kind).OrderBy(g => g.Key))
                {
                    ui.Heading(System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(group.Key.ToString()));
                    foreach (var row in group)
                    {
                        var mark = row.Healthy switch
                        {
                            true => $"[{Ui.Sage.ToMarkup()}]✓[/]",
                            false => $"[{Ui.Clay.ToMarkup()}]✗[/]",
                            null => $"[{slate}]·[/]",
                        };
                        var used = row.Selected ? $"[{Ui.Sage.ToMarkup()}]in use[/]" : $"[{slate}]not selected[/]";
                        ui.Write(new Markup($"  {mark} [bold]{Markup.Escape(row.Id)}[/]  [{slate}]{Markup.Escape(row.Version ?? "?")} · {Markup.Escape(row.Source)} ·[/] {used}\n"));
                        var details = new List<string>();
                        if (!string.IsNullOrWhiteSpace(row.Health))
                        {
                            details.Add(row.Healthy == false ? $"[{Ui.Clay.ToMarkup()}]{Markup.Escape(row.Health)}[/]" : $"[{slate}]{Markup.Escape(row.Health)}[/]");
                        }

                        if (!string.IsNullOrWhiteSpace(row.Description))
                        {
                            details.Add($"[{slate}]{Markup.Escape(row.Description)}[/]");
                        }

                        if (details.Count > 0)
                        {
                            ui.Write(new Padder(new Markup(string.Join("\n", details)), new Padding(4, 0, 0, 0)));
                        }
                    }
                }

                ui.Write(Text.NewLine);
                ui.Info($"Plugin folder: {runtime.Paths.Plugins} · select plugins under \"plugins\" in the config (`photo-clippy config show`).");
            }

            Presenter.Notes(ui, notes);
            ui.Emit("plugins", rows);
            return ExitCodes.Ok;
        }));
        return command;
    }

    private static Command BuildModelsCommand()
    {
        var command = new Command("models", "Manage the model files plugins need (e.g. the CLIP subject classifier).");

        var list = new Command("list", "Show required model files and whether they are present.");
        list.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var (models, fetcher, http) = await CollectModelsAsync(runtime, token).ConfigureAwait(false);
            using (http)
            {
                var rows = models.Select(m => new
                {
                    plugin = m.PluginId,
                    file = m.Asset.RelativePath,
                    path = fetcher.ResolvePath(m.Asset),
                    present = fetcher.IsPresent(m.Asset),
                    sizeBytes = m.Asset.SizeBytes,
                    source = m.Asset.Source,
                    license = m.Asset.License,
                }).ToList();
                if (ui.ShowHuman)
                {
                    if (rows.Count == 0)
                    {
                        ui.Info("No selected plugin needs model files.");
                    }
                    else
                    {
                        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35)
                            .AddColumn("Plugin").AddColumn("File").AddColumn(new TableColumn("Size").RightAligned()).AddColumn("Present").AddColumn("License");
                        foreach (var row in rows)
                        {
                            table.AddRow(
                                Markup.Escape(row.plugin),
                                Markup.Escape(row.file),
                                row.sizeBytes is { } size ? FormatBytes(size) : "?",
                                row.present ? $"[{Ui.Sage.ToMarkup()}]yes[/]" : $"[{Ui.Clay.ToMarkup()}]missing[/]",
                                Markup.Escape(row.license ?? string.Empty));
                        }

                        ui.Write(table);
                        ui.Info($"Models folder: {runtime.Paths.Models}");
                        if (rows.Any(r => !r.present))
                        {
                            ui.Info("Download with `photo-clippy models fetch`.");
                        }
                    }
                }

                ui.Emit("models", rows);
                return ExitCodes.Ok;
            }
        }));

        var fetch = new Command("fetch", "Download missing model files (SHA-256 verified).");
        var force = new Option<bool>("--force") { Description = "Re-download even if present." };
        var verify = new Option<bool>("--verify") { Description = "Hash-check files that are already present." };
        fetch.Options.Add(force);
        fetch.Options.Add(verify);
        fetch.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var (models, fetcher, http) = await CollectModelsAsync(runtime, token).ConfigureAwait(false);
            using (http)
            {
                if (models.Count == 0)
                {
                    ui.Info("No selected plugin needs model files.");
                    ui.Emit("models", Array.Empty<object>());
                    return ExitCodes.Ok;
                }

                IReadOnlyList<ModelFetchResult> results;
                if (ui.ShowHuman && ui.Console.Profile.Capabilities.Interactive)
                {
                    results = await ui.Console.Progress()
                        .AutoClear(false)
                        .Columns(new TaskDescriptionColumn(), new ProgressBarColumn { CompletedStyle = new Style(Ui.Fjord), FinishedStyle = new Style(Ui.Sage) }, new DownloadedColumn(), new TransferSpeedColumn())
                        .StartAsync(async context =>
                        {
                            var tasks = new Dictionary<string, ProgressTask>(StringComparer.OrdinalIgnoreCase);
                            var progress = new Progress<ModelFetchProgress>(p =>
                            {
                                if (!tasks.TryGetValue(p.RelativePath, out var task))
                                {
                                    task = tasks[p.RelativePath] = context.AddTask(Markup.Escape(p.RelativePath), maxValue: p.Total ?? 1);
                                }

                                if (p.Total is { } total)
                                {
                                    task.MaxValue = total;
                                }

                                task.Value = p.Downloaded;
                            });
                            return await fetcher.FetchAsync(models, parse.GetValue(force), parse.GetValue(verify), progress, token).ConfigureAwait(false);
                        }).ConfigureAwait(false);
                }
                else
                {
                    results = await fetcher.FetchAsync(models, parse.GetValue(force), parse.GetValue(verify), null, token).ConfigureAwait(false);
                }

                foreach (var result in results)
                {
                    switch (result.Status)
                    {
                        case ModelFetchStatus.Failed:
                            ui.Warn($"{result.Model.Asset.RelativePath}: {result.Error}");
                            break;
                        case ModelFetchStatus.Downloaded:
                            ui.Success($"{result.Model.Asset.RelativePath} downloaded");
                            break;
                        default:
                            ui.Success($"{result.Model.Asset.RelativePath} already present");
                            break;
                    }
                }

                ui.Emit("models", results.Select(r => new { plugin = r.Model.PluginId, file = r.Model.Asset.RelativePath, status = r.Status.ToString().ToLowerInvariant(), error = r.Error }));
                return results.Any(r => r.Status == ModelFetchStatus.Failed) ? ExitCodes.Error : ExitCodes.Ok;
            }
        }));

        command.Subcommands.Add(list);
        command.Subcommands.Add(fetch);
        return command;
    }

    private static Command BuildDoctorCommand()
    {
        var command = new Command("doctor", "Check the installation: runtime, folders, decoder, models, exiftool, plugins and cache.");
        command.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var checks = new List<(string Name, string Status, string Detail)>
            {
                ("runtime", "ok", $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · photo-clippy {AppRuntime.ToolVersion}"),
                ("config", "ok", runtime.ConfigFile ?? "none (defaults + PHOTO_CLIPPY_* environment)"),
            };

            foreach (var (name, dir) in new[] { ("home", runtime.Paths.Home), ("models dir", runtime.Paths.Models), ("state dir", runtime.Paths.State) })
            {
                checks.Add((name, IsWritable(dir, out var error) ? "ok" : "fail", error ?? dir));
            }

            try
            {
                var taxonomy = runtime.Taxonomy;
                checks.Add(("taxonomy", "ok", $"{taxonomy.Genres.Count} subject types · {runtime.Paths.Data}"));
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                checks.Add(("taxonomy", "fail", ex.Message));
            }

            checks.Add(DecoderCheck());

            try
            {
                var stats = runtime.Store.GetStats();
                checks.Add(("cache", "ok", $"{Ui.N(stats.MetadataRows)} files · {Ui.N(stats.FeatureRows)} analyses · {Ui.N(stats.Snapshots)} snapshots · {FormatBytes(stats.SizeBytes)} · {runtime.Store.DatabasePath}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(("cache", "fail", ex.Message));
            }

            var (rows, notes) = await PluginInventory.CollectAsync(runtime, checkHealth: true, token).ConfigureAwait(false);
            foreach (var row in rows)
            {
                var status = row.Healthy == false ? (row.Selected ? "warn" : "info") : "ok";
                var detail = row.Health ?? (row.Healthy == true ? "healthy" : "not checked");
                if (row.Models.Count > 0)
                {
                    detail += $" · {row.Models.Count} model file(s)";
                }

                checks.Add(($"{System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(row.Kind.ToString())}: {row.Id}{(row.Selected ? string.Empty : " (unused)")}", status, detail));
            }

            foreach (var note in notes)
            {
                checks.Add(("note", "warn", note));
            }

            if (ui.ShowHuman)
            {
                var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey35).AddColumn("Check").AddColumn("").AddColumn("Detail");
                foreach (var (name, status, detail) in checks)
                {
                    table.AddRow(Markup.Escape(name), StatusMarkup(status), Markup.Escape(detail));
                }

                ui.Write(table);
                if (rows.Any(r => r.Selected && r.Healthy == false && r.Models.Count > 0))
                {
                    ui.Info("Missing models? Run `photo-clippy models fetch`.");
                }
            }

            ui.Emit("doctor", checks.Select(c => new { name = c.Name, status = c.Status, detail = c.Detail }));
            return checks.Any(c => c.Status == "fail") ? ExitCodes.Error : ExitCodes.Ok;
        }));
        return command;
    }

    private static Command BuildConfigCommand()
    {
        var command = new Command("config", "Inspect or create the JSON config (every key can also be set via PHOTO_CLIPPY_<SECTION>__<KEY>).");

        var show = new Command("show", "Print the effective configuration (file + environment + defaults).");
        show.SetAction((parse, ct) => ExecuteAsync(parse, ct, (runtime, ui, _) =>
        {
            if (ui.Json)
            {
                ui.Emit("config", runtime.Options);
            }
            else
            {
                Console.Out.WriteLine(ConfigLoader.Serialize(runtime.Options));
            }

            return Task.FromResult(ExitCodes.Ok);
        }));

        var path = new Command("path", "Show which config file is used and where data lives.");
        path.SetAction((parse, ct) => ExecuteAsync(parse, ct, (runtime, ui, _) =>
        {
            var data = new
            {
                config = runtime.ConfigFile,
                searched = new[] { $"--config", $"${ConfigLoader.EnvPrefix}CONFIG", Path.Combine(Environment.CurrentDirectory, ConfigLoader.DefaultFileName), Path.Combine(runtime.Paths.Home, ConfigLoader.DefaultFileName) },
                runtime.Paths.Home,
                runtime.Paths.Database,
                runtime.Paths.Models,
                runtime.Paths.State,
                runtime.Paths.Data,
                runtime.Paths.Plugins,
            };
            if (ui.ShowHuman)
            {
                var grid = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
                grid.AddRow("[bold]config[/]", Markup.Escape(data.config ?? "none — using defaults"));
                grid.AddRow("[bold]searched[/]", Markup.Escape(string.Join("\n", data.searched)));
                grid.AddRow("[bold]home[/]", Markup.Escape(data.Home));
                grid.AddRow("[bold]cache[/]", Markup.Escape(data.Database));
                grid.AddRow("[bold]models[/]", Markup.Escape(data.Models));
                grid.AddRow("[bold]plugins[/]", Markup.Escape(data.Plugins));
                grid.AddRow("[bold]data[/]", Markup.Escape(data.Data));
                ui.Write(grid);
            }

            ui.Emit("paths", data);
            return Task.FromResult(ExitCodes.Ok);
        }));

        var init = new Command("init", "Write a starter config file (default: <home>/photo-clippy.json).");
        var target = new Argument<string?>("file") { Description = "Where to write it.", Arity = ArgumentArity.ZeroOrOne };
        var force = new Option<bool>("--force") { Description = "Overwrite an existing file." };
        init.Arguments.Add(target);
        init.Options.Add(force);
        init.SetAction((parse, ct) => ExecuteAsync(parse, ct, async (runtime, ui, token) =>
        {
            var file = Path.GetFullPath(parse.GetValue(target) ?? Path.Combine(runtime.Paths.Home, ConfigLoader.DefaultFileName));
            if (File.Exists(file) && !parse.GetValue(force))
            {
                throw new CliException(ExitCodes.Usage, $"{file} already exists.", "Use --force to overwrite.");
            }

            var starter = new AppOptions();
            starter.Library.Roots = [.. runtime.Options.Library.Roots];
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, ConfigLoader.Serialize(starter) + Environment.NewLine, token).ConfigureAwait(false);
            ui.Success($"Wrote {file}");
            ui.Emit("config", new { file });
            return ExitCodes.Ok;
        }));

        command.Subcommands.Add(show);
        command.Subcommands.Add(path);
        command.Subcommands.Add(init);
        return command;
    }

    private static Command BuildCacheCommand()
    {
        var command = new Command("cache", "Inspect or clear the analysis cache.");

        var stats = new Command("stats", "Show cache size and row counts.");
        stats.SetAction((parse, ct) => ExecuteAsync(parse, ct, (runtime, ui, _) =>
        {
            var s = runtime.Store.GetStats();
            if (ui.ShowHuman)
            {
                var grid = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn(new GridColumn().RightAligned());
                grid.AddRow("[bold]files[/]", Ui.N(s.MetadataRows));
                grid.AddRow("[bold]analyses[/]", Ui.N(s.FeatureRows));
                grid.AddRow("[bold]snapshots[/]", Ui.N(s.Snapshots));
                grid.AddRow("[bold]size[/]", FormatBytes(s.SizeBytes));
                ui.Write(grid);
                ui.Info(runtime.Store.DatabasePath);
            }

            ui.Emit("cache", new { s.MetadataRows, s.FeatureRows, s.Snapshots, s.SizeBytes, path = runtime.Store.DatabasePath });
            return Task.FromResult(ExitCodes.Ok);
        }));

        var clear = new Command("clear", "Delete all cached metadata, analyses and snapshots (the next scan starts from scratch).");
        var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation." };
        clear.Options.Add(yes);
        clear.SetAction((parse, ct) => ExecuteAsync(parse, ct, (runtime, ui, _) =>
        {
            if (!parse.GetValue(yes))
            {
                if (!ui.CanPrompt)
                {
                    throw new CliException(ExitCodes.Usage, "Refusing to clear the cache without confirmation.", "Pass --yes.");
                }

                if (!ui.Console.Confirm("Delete all cached analysis results?", defaultValue: false))
                {
                    return Task.FromResult(ExitCodes.Ok);
                }
            }

            runtime.Store.Clear();
            ui.Success("Cache cleared.");
            ui.Emit("cache", new { cleared = true });
            return Task.FromResult(ExitCodes.Ok);
        }));

        command.Subcommands.Add(stats);
        command.Subcommands.Add(clear);
        return command;
    }

    private static async Task<(IReadOnlyList<PluginModel> Models, ModelFetcher Fetcher, HttpClient Http)> CollectModelsAsync(AppRuntime runtime, CancellationToken cancellationToken)
    {
        await using var plugins = await runtime.CreatePluginsAsync(new HashSet<ComponentKind>(Enum.GetValues<ComponentKind>()), checkHealth: false, cancellationToken).ConfigureAwait(false);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"photo-clippy/{AppRuntime.ToolVersion}");
        return (ModelFetcher.Collect(plugins), new ModelFetcher(http, runtime.Paths.Models, runtime.CreateLogger("Models")), http);
    }

    private static (string, string, string) DecoderCheck()
    {
        try
        {
            using var bitmap = new SkiaSharp.SKBitmap(8, 8);
            return ("image decoder", "ok", $"SkiaSharp {(typeof(SkiaSharp.SKBitmap).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0]} native library loaded");
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException)
        {
            return ("image decoder", "fail", $"SkiaSharp native library could not be loaded ({ex.GetBaseException().Message}). Pixel analysis is unavailable; --no-frames still works.");
        }
    }

    private static bool IsWritable(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{directory}: {ex.Message}";
            return false;
        }
    }

    private static void Properties(Ui ui, string title, object value)
    {
        if (!ui.ShowHuman)
        {
            return;
        }

        var element = JsonSerializer.SerializeToElement(value, value.GetType(), ContractJson.Options);
        var grid = new Grid().AddColumn(new GridColumn().PadRight(2)).AddColumn();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() == 0)
                || (property.Value.ValueKind == JsonValueKind.Object && !property.Value.EnumerateObject().Any()))
            {
                continue;
            }

            var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
            if (text.Length > 160)
            {
                text = text[..157] + "…";
            }

            grid.AddRow($"[{Ui.Slate.ToMarkup()}]{Markup.Escape(property.Name)}[/]", Markup.Escape(text));
        }

        ui.Heading(title);
        ui.Write(grid);
    }

    private static void Genres(Ui ui, AppRuntime runtime, GenreAssignment assignment)
    {
        if (!ui.ShowHuman)
        {
            return;
        }

        ui.Heading("Subject");
        var label = assignment.Genre is { } id ? runtime.Taxonomy.Find(id)?.Name ?? id : "uncertain";
        ui.Console.MarkupLine($"[bold]{Markup.Escape(label)}[/] [{Ui.Slate.ToMarkup()}]{Ui.Pct(assignment.Confidence)} confidence{(assignment.Junk ? " · looks like a non-photo (screenshot/document)" : string.Empty)}[/]");
        var chart = new BarChart().Width(60);
        foreach (var (genre, p) in assignment.Probabilities.OrderByDescending(kv => kv.Value).Take(6))
        {
            chart.AddItem(runtime.Taxonomy.Find(genre)?.Name ?? genre, Math.Round(p * 100, 1), Ui.Fjord);
        }

        ui.Write(chart);
    }

    private static string StatusMarkup(string status) => status switch
    {
        "ok" => $"[{Ui.Sage.ToMarkup()}]✓[/]",
        "warn" => $"[{Ui.Sand.ToMarkup()}]![/]",
        "info" => $"[{Ui.Slate.ToMarkup()}]·[/]",
        _ => $"[{Ui.Clay.ToMarkup()}]✗[/]",
    };

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
