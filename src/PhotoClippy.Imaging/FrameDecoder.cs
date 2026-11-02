using System.Buffers;
using Microsoft.Extensions.Logging;
using PhotoClippy.Contracts;

namespace PhotoClippy.Imaging;

public sealed record DecodeRequest
{
    public required string Path { get; init; }

    public required int MaxEdge { get; init; }

    /// <summary>
    /// EXIF orientation 1..8 from metadata; when null, the orientation embedded in the decoded stream is used.
    /// </summary>
    public int? Orientation { get; init; }
}

public enum DecodeStatus
{
    Ok,
    Unsupported,
    Failed,
}

public sealed class DecodedFrame : IDisposable
{
    private byte[]? buffer;

    internal DecodedFrame(Frame frame, string method, byte[] buffer)
    {
        Frame = frame;
        Method = method;
        this.buffer = buffer;
    }

    public Frame Frame { get; }

    /// <summary>"jpeg", "raw-preview", "tiff" or "codec".</summary>
    public string Method { get; }

    /// <summary>Returns the pooled RGB24 buffer to <see cref="ArrayPool{T}.Shared"/>; idempotent.</summary>
    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref buffer, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

public sealed record DecodeOutcome(DecodeStatus Status, DecodedFrame? Frame, string? Error);

/// <summary>
/// Decodes a capture once into an orientation-corrected, downscaled RGB24 <see cref="Frame"/>.
/// Stateless and thread-safe; bad files yield <see cref="DecodeStatus.Failed"/> instead of exceptions.
/// </summary>
public sealed class FrameDecoder
{
    private readonly ILogger? logger;

    public FrameDecoder(ILogger? logger = null)
    {
        this.logger = logger;
    }

    public bool IsSupported(string path) =>
        !string.IsNullOrEmpty(path) && FormatCatalog.IsSupportedExtension(FormatCatalog.Extension(path));

    public DecodeOutcome Decode(DecodeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.Path, nameof(request));
        if (request.MaxEdge <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxEdge, "MaxEdge must be positive.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var extension = FormatCatalog.Extension(request.Path);
        if (FormatCatalog.IsVideo(extension))
        {
            return Unsupported(request.Path, "video files are not decoded");
        }

        var orientation = request.Orientation is >= 1 and <= 8 ? request.Orientation : null;
        try
        {
            var frame = DecodePipeline.Run(request.Path, extension, request.MaxEdge, orientation, cancellationToken);
            logger?.LogDebug(
                "Decoded {Path} via {Method} to {Width}x{Height} (source {SourceWidth}x{SourceHeight})",
                request.Path,
                frame.Method,
                frame.Frame.Width,
                frame.Frame.Height,
                frame.Frame.SourceWidth,
                frame.Frame.SourceHeight);
            return new DecodeOutcome(DecodeStatus.Ok, frame, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnsupportedImageException ex)
        {
            return Unsupported(request.Path, ex.Message);
        }
        catch (ImageDecodeException ex)
        {
            return Failed(request.Path, ex.Message, null);
        }
        catch (FileNotFoundException ex)
        {
            return Failed(request.Path, "file not found", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Failed(request.Path, "file not found", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(request.Path, "access denied", ex);
        }
        catch (EndOfStreamException ex)
        {
            return Failed(request.Path, "truncated image data", ex);
        }
        catch (InvalidDataException ex)
        {
            return Failed(request.Path, "corrupt image data: " + ex.Message, ex);
        }
        catch (IOException ex)
        {
            return Failed(request.Path, "I/O error: " + ex.Message, ex);
        }
        catch (OutOfMemoryException ex)
        {
            return Failed(request.Path, "out of memory", ex);
        }
        catch (Exception ex)
        {
            return Failed(request.Path, ex.GetType().Name + ": " + ex.Message, ex);
        }
    }

    private DecodeOutcome Unsupported(string path, string reason)
    {
        logger?.LogDebug("Unsupported image {Path}: {Reason}", path, reason);
        return new DecodeOutcome(DecodeStatus.Unsupported, null, reason);
    }

    private DecodeOutcome Failed(string path, string reason, Exception? exception)
    {
        logger?.LogDebug(exception, "Failed to decode {Path}: {Reason}", path, reason);
        return new DecodeOutcome(DecodeStatus.Failed, null, reason);
    }
}

internal sealed class ImageDecodeException(string message) : Exception(message);

internal sealed class UnsupportedImageException(string message) : Exception(message);
