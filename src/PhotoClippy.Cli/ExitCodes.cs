namespace PhotoClippy.Cli;

internal static class ExitCodes
{
    public const int Ok = 0;
    /// <summary>Unexpected failure.</summary>
    public const int Error = 1;
    /// <summary>Invalid usage or configuration (bad option, unparseable lens shorthand, unknown body).</summary>
    public const int Usage = 2;
    /// <summary>Nothing to work with: no photos found, or no stored analysis yet.</summary>
    public const int NoData = 3;
    /// <summary>Completed, but some parts failed (a renderer, a model download, an unhealthy plugin in doctor).</summary>
    public const int Partial = 4;
    public const int Cancelled = 130;
}

/// <summary>An expected failure with a user-facing message and exit code (no stack trace).</summary>
internal sealed class CliException(int exitCode, string message, string? hint = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string? Hint { get; } = hint;
}