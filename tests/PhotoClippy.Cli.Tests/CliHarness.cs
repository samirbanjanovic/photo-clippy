using System.Text.RegularExpressions;
using PhotoClippy.Core.Configuration;
using PhotoClippy.Testing;
using Spectre.Console;

namespace PhotoClippy.Cli.Tests;

internal sealed record CliResult(int ExitCode, string Out, string Error)
{
    public string All => Out + "\n" + Error;
}

/// <summary>Temp library of tagged TIFFs, an explicit config file and an isolated home; runs the CLI in-process with captured output.</summary>
internal sealed partial class CliHarness : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public CliHarness()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "cli-fixtures", Guid.NewGuid().ToString("N"));
        Library = Path.Combine(Root, "lib");
        Home = Path.Combine(Root, "home");
        Output = Path.Combine(Root, "report");
        ConfigFile = Path.Combine(Root, "photo-clippy.json");
        Directory.CreateDirectory(Library);

        var focals = new[] { 24, 35, 35, 50 };
        for (var i = 0; i < focals.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(Library, $"img{i}.tif"), ExifWriter.BuildTiff(new ExifSpec
            {
                Make = "NIKON CORPORATION",
                Model = "NIKON Z 6_2",
                LensMake = "Nikon",
                LensModel = "NIKKOR Z 24-70mm f/4 S",
                LensSpec = (24, 70, 4, 4),
                FocalLength = focals[i],
                FocalLength35 = focals[i],
                FNumber = 4,
                Iso = 3200,
                ExposureTime = 1.0 / 60,
                DateTimeOriginal = new DateTime(2025, 6, 1 + i, 18, 0, 0),
            }));
        }

        Options = new AppOptions
        {
            Library = new LibraryOptions { Roots = [Library] },
            Analysis = new AnalysisOptions { Frames = false },
            Recommendation = new RecommendationOptions { BootstrapReplicates = 0, Prompt = false },
            Report = new ReportOptions { Output = Output },
        };
        Options.Plugins.Reports = ["builtin.report.html", "builtin.report.json"];
        SaveConfig();
    }

    public string Root { get; }
    public string Library { get; }
    public string Home { get; }
    public string Output { get; }
    public string ConfigFile { get; }
    public AppOptions Options { get; }

    public void SaveConfig() => File.WriteAllText(ConfigFile, ConfigLoader.Serialize(Options));

    public string MakeDir(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Runs with <c>--config</c> and <c>--home</c> pinned to the fixture so the user's real configuration is never read.</summary>
    public Task<CliResult> RunAsync(params string[] args) => InvokeAsync([.. args, "--config", ConfigFile, "--home", Home]);

    public static async Task<CliResult> InvokeAsync(string[] args)
    {
        await Gate.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalAnsi = AnsiConsole.Console;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(stdout),
            });
            console.Profile.Width = 1000;
            AnsiConsole.Console = console;

            var exit = await CliApplication.RunAsync(args);
            return new CliResult(exit, StripAnsi(stdout.ToString()), StripAnsi(stderr.ToString()));
        }
        finally
        {
            AnsiConsole.Console = originalAnsi;
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Gate.Release();
        }
    }

    public static string StripAnsi(string text) => AnsiPattern().Replace(text, string.Empty);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiPattern();
}
