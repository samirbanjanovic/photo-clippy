namespace PhotoClippy.Plugins.Metadata;

/// <summary>Settings for <c>builtin.metadata.exiftool</c> (config section <c>plugins:builtin.metadata.exiftool</c>).</summary>
public sealed record ExifToolSettings
{
    /// <summary>exiftool executable (resolved via PATH) or full path; a path to the Perl script or a <c>.pl</c> file is run with <c>perl</c>.</summary>
    public string ExecutablePath { get; init; } = "exiftool";

    /// <summary>Maximum time for one batch before the exiftool process is killed and restarted.</summary>
    public int TimeoutSeconds { get; init; } = 120;
}
