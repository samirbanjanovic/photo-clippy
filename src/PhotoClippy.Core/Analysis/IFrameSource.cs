using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Analysis;

public enum FrameDecodeStatus
{
    Ok,
    /// <summary>The format cannot be decoded by this source (e.g. RAW without an embedded preview).</summary>
    Unsupported,
    Failed,
}

/// <summary>A decoded frame plus a release callback for pooled buffers.</summary>
public sealed class FrameLease(Frame frame, string? method = null, Action? release = null) : IDisposable
{
    private Action? _release = release;

    public Frame Frame { get; } = frame;
    public string? Method { get; } = method;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

public readonly record struct FrameDecodeResult(FrameDecodeStatus Status, FrameLease? Lease, string? Error)
{
    public static FrameDecodeResult Ok(FrameLease lease) => new(FrameDecodeStatus.Ok, lease, null);
    public static FrameDecodeResult Unsupported(string? error = null) => new(FrameDecodeStatus.Unsupported, null, error);
    public static FrameDecodeResult Failed(string error) => new(FrameDecodeStatus.Failed, null, error);
}

/// <summary>Decodes an orientation-corrected, downscaled sRGB frame. Implementations must be thread-safe.</summary>
public interface IFrameSource
{
    /// <summary>Identifies the decoder; part of the cache key for decode-failure records.</summary>
    string Id { get; }

    bool CanDecode(SourceFile file);

    FrameDecodeResult Decode(SourceFile file, int maxEdge, CancellationToken cancellationToken = default);
}
