using System.Buffers;

namespace PhotoClippy.Imaging;

internal readonly record struct JpegInfo(int Width, int Height, int Components)
{
    public Size Size => new(Width, Height);
}

/// <summary>Reads JPEG frame headers without decoding; only baseline/extended/progressive 8-bit images are accepted.</summary>
internal static class JpegProbe
{
    private const int ProbeWindow = 64 * 1024;

    public static bool StartsWithSoi(ReadOnlySpan<byte> data) =>
        data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;

    public static JpegInfo? Probe(ReadOnlySpan<byte> data) => Probe(data, null, 0, data.Length);

    public static JpegInfo? Probe(SourceFile file, long offset, long length)
    {
        if (offset < 0 || length < 4 || offset >= file.Length)
        {
            return null;
        }

        length = Math.Min(length, file.Length - offset);
        var windowLength = (int)Math.Min(length, ProbeWindow);
        var window = ArrayPool<byte>.Shared.Rent(windowLength);
        try
        {
            var read = file.Read(offset, window.AsSpan(0, windowLength));
            return Probe(window.AsSpan(0, read), file, offset, length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(window);
        }
    }

    /// <summary>Returns the index just past the EOI marker of the JPEG starting at <paramref name="start"/>, or -1.</summary>
    public static int FindEnd(ReadOnlySpan<byte> data, int start)
    {
        if (!StartsWithSoi(data[start..]))
        {
            return -1;
        }

        var position = start + 2;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                return -1;
            }

            var marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker == 0xD9)
            {
                return position + 2;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            var segmentLength = (data[position + 2] << 8) | data[position + 3];
            if (segmentLength < 2)
            {
                return -1;
            }

            position += 2 + segmentLength;
            if (marker != 0xDA)
            {
                continue;
            }

            position = SkipEntropyData(data, position);
            if (position < 0)
            {
                return -1;
            }
        }

        return -1;
    }

    private static int SkipEntropyData(ReadOnlySpan<byte> data, int position)
    {
        while (position < data.Length)
        {
            var index = data[position..].IndexOf((byte)0xFF);
            if (index < 0)
            {
                return -1;
            }

            position += index;
            if (position + 1 >= data.Length)
            {
                return -1;
            }

            var next = data[position + 1];
            if (next == 0x00 || next is >= 0xD0 and <= 0xD7)
            {
                position += 2;
            }
            else if (next == 0xFF)
            {
                position++;
            }
            else
            {
                return position;
            }
        }

        return -1;
    }

    private static JpegInfo? Probe(ReadOnlySpan<byte> window, SourceFile? file, long baseOffset, long length)
    {
        Span<byte> buffer = stackalloc byte[8];
        if (!Fetch(window, file, baseOffset, length, 0, buffer[..3]) || !StartsWithSoi(buffer))
        {
            return null;
        }

        long position = 2;
        for (var guard = 0; guard < 1024; guard++)
        {
            if (!Fetch(window, file, baseOffset, length, position, buffer[..4]) || buffer[0] != 0xFF)
            {
                return null;
            }

            var marker = buffer[1];
            if (marker == 0xFF)
            {
                position++;
                continue;
            }

            if (marker is 0x01 or 0xD8 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            if (marker is 0xD9 or 0xDA)
            {
                return null;
            }

            var segmentLength = (buffer[2] << 8) | buffer[3];
            if (segmentLength < 2)
            {
                return null;
            }

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (!Fetch(window, file, baseOffset, length, position + 4, buffer[..6]))
                {
                    return null;
                }

                var precision = buffer[0];
                var height = (buffer[1] << 8) | buffer[2];
                var width = (buffer[3] << 8) | buffer[4];
                var components = buffer[5];
                if (precision != 8 || width == 0 || height == 0 || components is not (1 or 3))
                {
                    return null;
                }

                return new JpegInfo(width, height, components);
            }

            if (marker is >= 0xC3 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return null;
            }

            position += 2 + segmentLength;
        }

        return null;
    }

    private static bool Fetch(ReadOnlySpan<byte> window, SourceFile? file, long baseOffset, long length, long position, Span<byte> destination)
    {
        if (position < 0 || position + destination.Length > length)
        {
            return false;
        }

        if (position + destination.Length <= window.Length)
        {
            window.Slice((int)position, destination.Length).CopyTo(destination);
            return true;
        }

        return file is not null && file.Read(baseOffset + position, destination) == destination.Length;
    }
}
