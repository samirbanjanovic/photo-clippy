namespace PhotoClippy.Core.Pipeline;

public static class PipelineStages
{
    public const string Discover = "discover";
    public const string Metadata = "metadata";
    public const string Normalize = "normalize";
    public const string Frames = "frames";
    public const string Profile = "profile";
    public const string Recommend = "recommend";
    public const string Render = "render";
}

/// <summary>Progress of a pipeline stage. <see cref="Total"/> is null while unknown (e.g. during discovery).</summary>
public sealed record PipelineProgress(string Stage, long Completed, long? Total, string? Detail = null, long Cached = 0);
