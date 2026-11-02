using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace PhotoClippy.Imaging;

/// <summary>An open image file with a cached head and positional reads (safe for network shares).</summary>
internal sealed class SourceFile : IDisposable
{
    private const int HeadSize = 256 * 1024;
    private const long MaxWholeFileBytes = 1L << 30;

    private readonly SafeFileHandle handle;
    private byte[]? head;
    private int headLength;

    private SourceFile(SafeFileHandle handle, long length)
    {
        this.handle = handle;
        Length = length;
    }

    public long Length { get; }

    public ReadOnlySpan<byte> Head => head.AsSpan(0, headLength);

    public static SourceFile Open(string path, bool randomAccess)
    {
        var options = randomAccess ? FileOptions.RandomAccess : FileOptions.SequentialScan;
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, options);
        try
        {
            var file = new SourceFile(handle, RandomAccess.GetLength(handle));
            file.LoadHead();
            return file;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= Length || destination.IsEmpty)
        {
            return 0;
        }

        if (offset + destination.Length <= headLength)
        {
            head.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return destination.Length;
        }

        var total = 0;
        while (total < destination.Length)
        {
            var read = RandomAccess.Read(handle, destination[total..], offset + total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public void ReadExactly(long offset, Span<byte> destination)
    {
        if (Read(offset, destination) != destination.Length)
        {
            throw new ImageDecodeException("truncated file");
        }
    }

    /// <summary>Reads [offset, offset+length) into a pooled buffer; the caller returns it to <see cref="ArrayPool{T}.Shared"/>.</summary>
    public byte[] ReadRange(long offset, long length)
    {
        if (offset < 0 || length <= 0 || offset + length > Length)
        {
            throw new ImageDecodeException("embedded image lies outside the file");
        }

        if (length > MaxWholeFileBytes)
        {
            throw new ImageDecodeException("embedded image is too large");
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            ReadExactly(offset, buffer.AsSpan(0, (int)length));
            return buffer;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>Reads the whole file sequentially into a pooled buffer.</summary>
    public byte[] ReadAll(out int length, CancellationToken cancellationToken)
    {
        if (Length > MaxWholeFileBytes)
        {
            throw new ImageDecodeException("file is too large");
        }

        length = (int)Length;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            Head.CopyTo(buffer);
            var position = headLength;
            while (position < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle, buffer.AsSpan(position, Math.Min(length - position, 4 * 1024 * 1024)), position);
                if (read <= 0)
                {
                    length = position;
                    break;
                }

                position += read;
            }

            return buffer;
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref head, null);
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        headLength = 0;
        handle.Dispose();
    }

    private void LoadHead()
    {
        var size = (int)Math.Min(Length, HeadSize);
        head = ArrayPool<byte>.Shared.Rent(Math.Max(1, size));
        headLength = 0;
        var total = 0;
        while (total < size)
        {
            var read = RandomAccess.Read(handle, head.AsSpan(total, size - total), total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        headLength = total;
    }
}
