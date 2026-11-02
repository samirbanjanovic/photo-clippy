using System.Buffers;
using System.IO.Compression;

namespace PhotoClippy.Imaging;

/// <summary>Buffered sequential reader over one strip/tile byte range.</summary>
internal sealed class ChunkInput : IDisposable
{
    private readonly SourceFile file;
    private readonly long end;
    private byte[]? buffer;
    private long position;
    private int bufferPosition;
    private int bufferLength;

    public ChunkInput(SourceFile file, long offset, long length, int bufferSize)
    {
        this.file = file;
        position = offset;
        end = Math.Min(file.Length, offset + length);
        var size = (int)Math.Clamp(end - offset, 1, bufferSize);
        buffer = ArrayPool<byte>.Shared.Rent(size);
    }

    public int ReadByte()
    {
        if (bufferPosition >= bufferLength && !Fill())
        {
            return -1;
        }

        return buffer![bufferPosition++];
    }

    public int Read(Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            if (bufferPosition >= bufferLength && !Fill())
            {
                break;
            }

            var count = Math.Min(destination.Length - total, bufferLength - bufferPosition);
            buffer.AsSpan(bufferPosition, count).CopyTo(destination[total..]);
            bufferPosition += count;
            total += count;
        }

        return total;
    }

    public void Dispose()
    {
        var rented = buffer;
        buffer = null;
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool Fill()
    {
        if (buffer is null || position >= end)
        {
            return false;
        }

        var count = (int)Math.Min(buffer.Length, end - position);
        var read = file.Read(position, buffer.AsSpan(0, count));
        if (read <= 0)
        {
            position = end;
            return false;
        }

        position += read;
        bufferPosition = 0;
        bufferLength = read;
        return true;
    }
}

internal sealed class ChunkInputStream(ChunkInput input) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => input.Read(buffer);

    public override int ReadByte() => input.ReadByte();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Streams the decompressed bytes of one strip or tile.</summary>
internal abstract class ChunkDecompressor : IDisposable
{
    public static ChunkDecompressor Create(int compression, ChunkInput input) => compression switch
    {
        1 => new RawDecompressor(input),
        5 => new LzwDecompressor(input),
        8 or 32946 => new DeflateDecompressor(input),
        32773 => new PackBitsDecompressor(input),
        _ => throw new ImageDecodeException("unsupported TIFF compression " + compression.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    };

    public abstract int Read(Span<byte> destination);

    public void ReadExactly(Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = Read(destination[total..]);
            if (read <= 0)
            {
                throw new ImageDecodeException("truncated or corrupt TIFF image data");
            }

            total += read;
        }
    }

    public abstract void Dispose();
}

internal sealed class RawDecompressor(ChunkInput input) : ChunkDecompressor
{
    public override int Read(Span<byte> destination) => input.Read(destination);

    public override void Dispose() => input.Dispose();
}

internal sealed class DeflateDecompressor : ChunkDecompressor
{
    private readonly ChunkInput input;
    private readonly ZLibStream stream;

    public DeflateDecompressor(ChunkInput input)
    {
        this.input = input;
        stream = new ZLibStream(new ChunkInputStream(input), CompressionMode.Decompress);
    }

    public override int Read(Span<byte> destination) => stream.Read(destination);

    public override void Dispose()
    {
        stream.Dispose();
        input.Dispose();
    }
}

internal sealed class PackBitsDecompressor(ChunkInput input) : ChunkDecompressor
{
    private int literalRemaining;
    private int runRemaining;
    private byte runValue;

    public override int Read(Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            if (literalRemaining > 0)
            {
                var read = input.Read(destination.Slice(total, Math.Min(literalRemaining, destination.Length - total)));
                if (read <= 0)
                {
                    break;
                }

                literalRemaining -= read;
                total += read;
                continue;
            }

            if (runRemaining > 0)
            {
                var count = Math.Min(runRemaining, destination.Length - total);
                destination.Slice(total, count).Fill(runValue);
                runRemaining -= count;
                total += count;
                continue;
            }

            var header = input.ReadByte();
            if (header < 0)
            {
                break;
            }

            var n = (sbyte)header;
            if (n >= 0)
            {
                literalRemaining = n + 1;
            }
            else if (n != -128)
            {
                var value = input.ReadByte();
                if (value < 0)
                {
                    break;
                }

                runValue = (byte)value;
                runRemaining = 1 - n;
            }
        }

        return total;
    }

    public override void Dispose() => input.Dispose();
}

/// <summary>TIFF LZW (MSB-first, early change, 9..12-bit codes).</summary>
internal sealed class LzwDecompressor : ChunkDecompressor
{
    private const int ClearCode = 256;
    private const int EndCode = 257;
    private const int TableSize = 4096;

    private readonly ChunkInput input;
    private readonly short[] prefix = ArrayPool<short>.Shared.Rent(TableSize);
    private readonly byte[] suffix = ArrayPool<byte>.Shared.Rent(TableSize);
    private readonly byte[] first = ArrayPool<byte>.Shared.Rent(TableSize);
    private readonly ushort[] lengths = ArrayPool<ushort>.Shared.Rent(TableSize);
    private readonly byte[] pending = ArrayPool<byte>.Shared.Rent(TableSize + 1);
    private int pendingStart;
    private int pendingEnd;
    private uint bitBuffer;
    private int bitCount;
    private int codeWidth = 9;
    private int next = 258;
    private int previous = -1;
    private bool ended;
    private bool started;
    private bool disposed;

    public LzwDecompressor(ChunkInput input)
    {
        this.input = input;
        for (var i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            first[i] = (byte)i;
            lengths[i] = 1;
        }
    }

    public override int Read(Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            if (pendingStart < pendingEnd)
            {
                var count = Math.Min(pendingEnd - pendingStart, destination.Length - total);
                pending.AsSpan(pendingStart, count).CopyTo(destination[total..]);
                pendingStart += count;
                total += count;
                continue;
            }

            if (ended || !DecodeNext())
            {
                ended = true;
                break;
            }
        }

        return total;
    }

    public override void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ArrayPool<short>.Shared.Return(prefix);
        ArrayPool<byte>.Shared.Return(suffix);
        ArrayPool<byte>.Shared.Return(first);
        ArrayPool<ushort>.Shared.Return(lengths);
        ArrayPool<byte>.Shared.Return(pending);
        input.Dispose();
    }

    private bool DecodeNext()
    {
        while (true)
        {
            var code = NextCode();
            if (code < 0 || code == EndCode)
            {
                return false;
            }

            if (code == ClearCode)
            {
                codeWidth = 9;
                next = 258;
                previous = -1;
                continue;
            }

            if (previous < 0)
            {
                if (code > 255)
                {
                    throw new ImageDecodeException("corrupt LZW data");
                }

                pending[0] = (byte)code;
                pendingStart = 0;
                pendingEnd = 1;
                previous = code;
                return true;
            }

            if (code > next || (code == next && next >= TableSize))
            {
                throw new ImageDecodeException("corrupt LZW data");
            }

            if (next < TableSize)
            {
                var firstByte = code < next ? first[code] : first[previous];
                prefix[next] = (short)previous;
                suffix[next] = firstByte;
                first[next] = first[previous];
                lengths[next] = (ushort)(lengths[previous] + 1);
                next++;
                if (next >= (1 << codeWidth) - 1 && codeWidth < 12)
                {
                    codeWidth++;
                }
            }

            Emit(code);
            previous = code;
            return true;
        }
    }

    private void Emit(int code)
    {
        var length = lengths[code];
        var current = code;
        for (var i = length - 1; i >= 0; i--)
        {
            pending[i] = suffix[current];
            current = prefix[current];
        }

        pendingStart = 0;
        pendingEnd = length;
    }

    private int NextCode()
    {
        while (bitCount < codeWidth)
        {
            var value = input.ReadByte();
            if (value < 0)
            {
                return -1;
            }

            if (!started)
            {
                started = true;
                if (value == 0)
                {
                    var second = input.ReadByte();
                    if (second < 0)
                    {
                        return -1;
                    }

                    if ((second & 1) != 0)
                    {
                        throw new ImageDecodeException("old-style LZW TIFF is not supported");
                    }

                    bitBuffer = (uint)((value << 8) | second);
                    bitCount = 16;
                    continue;
                }
            }

            bitBuffer = (bitBuffer << 8) | (uint)value;
            bitCount += 8;
        }

        bitCount -= codeWidth;
        var code = (int)((bitBuffer >> bitCount) & ((1u << codeWidth) - 1));
        bitBuffer &= (1u << bitCount) - 1;
        return code;
    }
}
