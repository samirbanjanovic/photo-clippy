using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;
using PhotoClippy.Imaging;

namespace PhotoClippy.Core.Analysis;

/// <summary>Built-in <see cref="IFrameSource"/> backed by <see cref="FrameDecoder"/> (JPEG, RAW previews, TIFF, PNG/WebP/HEIF via Skia).</summary>
public sealed class ImagingFrameSource(ILogger? logger = null) : IFrameSource
{
    private readonly FrameDecoder _decoder = new(logger);

    public string Id => "builtin.imaging@1";

    public bool CanDecode(SourceFile file) => _decoder.IsSupported(file.Path);

    public FrameDecodeResult Decode(SourceFile file, int maxEdge, CancellationToken cancellationToken = default)
    {
        var outcome = _decoder.Decode(new DecodeRequest { Path = file.Path, MaxEdge = maxEdge }, cancellationToken);
        return outcome.Status switch
        {
            DecodeStatus.Ok when outcome.Frame is { } decoded => FrameDecodeResult.Ok(new FrameLease(decoded.Frame, decoded.Method, decoded.Dispose)),
            DecodeStatus.Unsupported => FrameDecodeResult.Unsupported(outcome.Error),
            _ => FrameDecodeResult.Failed(outcome.Error ?? "decode failed"),
        };
    }
}
