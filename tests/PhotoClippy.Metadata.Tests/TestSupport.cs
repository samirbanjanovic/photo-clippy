using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PhotoClippy.Contracts;
using SkiaSharp;

namespace PhotoClippy.Metadata.Tests;

/// <summary>A unique fixture directory below the test output folder (never the OS temp dir); deleted on dispose.</summary>
internal sealed class FixtureDirectory : IDisposable
{
    public FixtureDirectory(string? name = null)
    {
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Write(string fileName, byte[] content)
    {
        var path = System.IO.Path.Combine(Path, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string WriteText(string fileName, string content)
    {
        var path = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Fixtures
{
    private static readonly Lazy<byte[]> BaseJpegBytes = new(() => EncodeJpeg(64, 48));

    /// <summary>A small plain JPEG (64×48, no metadata) produced by SkiaSharp.</summary>
    public static byte[] BaseJpeg => BaseJpegBytes.Value;

    public static byte[] EncodeJpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(90, 140, 200));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }

    public static SourceFile Source(string path)
    {
        var info = new FileInfo(path);
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return new SourceFile
        {
            Path = path,
            Size = info.Exists ? info.Length : 0,
            LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.UnixEpoch,
            Kind = ext switch
            {
                ".jpg" or ".jpeg" or ".jpe" => MediaKind.Jpeg,
                ".heic" or ".heif" or ".hif" or ".avif" => MediaKind.Heif,
                ".tif" or ".tiff" => MediaKind.Tiff,
                ".png" => MediaKind.Png,
                ".webp" => MediaKind.Other,
                _ => MediaKind.Raw,
            },
        };
    }

    public static IReadOnlyList<SourceFile> Sources(params string[] paths) => [.. paths.Select(Source)];

    public static PluginContext Context(object? settings = null)
    {
        var root = System.IO.Path.Combine(AppContext.BaseDirectory, "plugin-context");
        return new PluginContext
        {
            Settings = settings is null ? default : JsonSerializer.SerializeToElement(settings, ContractJson.Options),
            DataDirectory = root,
            ModelsDirectory = root,
            StateDirectory = root,
            LoggerFactory = NullLoggerFactory.Instance,
            Taxonomy = new GenreTaxonomy(),
        };
    }

    /// <summary>Inserts APP1 segments (EXIF and/or XMP) right after the SOI marker.</summary>
    public static byte[] JpegWith(byte[]? tiff = null, string? xmp = null, byte[]? jpeg = null)
    {
        jpeg ??= BaseJpeg;
        using var ms = new MemoryStream();
        ms.Write(jpeg.AsSpan(0, 2));
        if (tiff is not null)
        {
            WriteApp1(ms, "Exif\0\0"u8, tiff);
        }

        if (xmp is not null)
        {
            WriteApp1(ms, "http://ns.adobe.com/xap/1.0/\0"u8, Encoding.UTF8.GetBytes(xmp));
        }

        ms.Write(jpeg.AsSpan(2));
        return ms.ToArray();
    }

    private static void WriteApp1(MemoryStream ms, ReadOnlySpan<byte> header, byte[] payload)
    {
        Span<byte> marker = stackalloc byte[4];
        marker[0] = 0xFF;
        marker[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(marker[2..], checked((ushort)(2 + header.Length + payload.Length)));
        ms.Write(marker);
        ms.Write(header);
        ms.Write(payload);
    }

    /// <summary>An XMP packet whose single rdf:Description carries <paramref name="attributes"/>.</summary>
    public static string Xmp(string attributes) =>
        $"""
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about=""
            xmlns:xmp="http://ns.adobe.com/xap/1.0/"
            xmlns:aux="http://ns.adobe.com/exif/1.0/aux/"
            xmlns:exifEX="http://cipa.jp/exif/1.0/"
            {attributes}/>
         </rdf:RDF>
        </x:xmpmeta>
        <?xpacket end="w"?>
        """;
}

internal enum Ifd
{
    Zero,
    Exif,
    Gps,
}

/// <summary>
/// Little-endian TIFF/EXIF builder for tags <see cref="PhotoClippy.Testing.ExifWriter"/> does not cover
/// (APEX values, metering, white balance, ISO fallbacks, alternative dates, raw 0/0 rationals, …).
/// </summary>
internal sealed class TiffBuilder
{
    private const ushort AsciiType = 2, ShortType = 3, LongType = 4, RationalType = 5, SRationalType = 10;

    private readonly Dictionary<Ifd, List<(ushort Tag, ushort Type, uint Count, byte[] Data)>> _ifds = new()
    {
        [Ifd.Zero] = [],
        [Ifd.Exif] = [],
        [Ifd.Gps] = [],
    };

    public TiffBuilder Ascii(Ifd ifd, ushort tag, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value + "\0");
        return Add(ifd, tag, AsciiType, (uint)bytes.Length, bytes);
    }

    public TiffBuilder Short(Ifd ifd, ushort tag, params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return Add(ifd, tag, ShortType, (uint)values.Length, bytes);
    }

    public TiffBuilder Long(Ifd ifd, ushort tag, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return Add(ifd, tag, LongType, 1, bytes);
    }

    public TiffBuilder Rational(Ifd ifd, ushort tag, params (uint Num, uint Den)[] values)
    {
        var bytes = new byte[values.Length * 8];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 8), values[i].Num);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((i * 8) + 4), values[i].Den);
        }

        return Add(ifd, tag, RationalType, (uint)values.Length, bytes);
    }

    public TiffBuilder SRational(Ifd ifd, ushort tag, int num, int den)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, num);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), den);
        return Add(ifd, tag, SRationalType, 1, bytes);
    }

    public byte[] Build()
    {
        var ifd0 = _ifds[Ifd.Zero].ToList();
        var exif = _ifds[Ifd.Exif];
        var gps = _ifds[Ifd.Gps];
        if (exif.Count > 0)
        {
            ifd0.Add((0x8769, LongType, 1, new byte[4]));
        }

        if (gps.Count > 0)
        {
            ifd0.Add((0x8825, LongType, 1, new byte[4]));
        }

        var off0 = 8u;
        var offExif = off0 + BlockSize(ifd0);
        var offGps = offExif + BlockSize(exif);
        SetPointer(ifd0, 0x8769, offExif);
        SetPointer(ifd0, 0x8825, offGps);

        using var ms = new MemoryStream();
        ms.Write("II"u8);
        ms.Write([42, 0]);
        WriteU32(ms, off0);
        WriteIfd(ms, ifd0, off0);
        WriteIfd(ms, exif, offExif);
        WriteIfd(ms, gps, offGps);
        return ms.ToArray();
    }

    private TiffBuilder Add(Ifd ifd, ushort tag, ushort type, uint count, byte[] data)
    {
        var list = _ifds[ifd];
        list.RemoveAll(e => e.Tag == tag);
        list.Add((tag, type, count, data));
        return this;
    }

    private static void SetPointer(List<(ushort Tag, ushort Type, uint Count, byte[] Data)> entries, ushort tag, uint offset)
    {
        var i = entries.FindIndex(e => e.Tag == tag);
        if (i >= 0)
        {
            var data = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(data, offset);
            entries[i] = (tag, LongType, 1, data);
        }
    }

    private static uint BlockSize(List<(ushort Tag, ushort Type, uint Count, byte[] Data)> entries)
    {
        if (entries.Count == 0)
        {
            return 0;
        }

        var size = 2u + (12u * (uint)entries.Count) + 4u;
        foreach (var e in entries)
        {
            if (e.Data.Length > 4)
            {
                size += (uint)(e.Data.Length + (e.Data.Length & 1));
            }
        }

        return size;
    }

    private static void WriteIfd(MemoryStream ms, List<(ushort Tag, ushort Type, uint Count, byte[] Data)> entries, uint offset)
    {
        if (entries.Count == 0)
        {
            return;
        }

        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        var dataOffset = offset + 2u + (12u * (uint)entries.Count) + 4u;
        WriteU16(ms, (ushort)entries.Count);
        var blobs = new List<byte[]>();
        foreach (var e in entries)
        {
            WriteU16(ms, e.Tag);
            WriteU16(ms, e.Type);
            WriteU32(ms, e.Count);
            if (e.Data.Length <= 4)
            {
                var inline = new byte[4];
                e.Data.CopyTo(inline, 0);
                ms.Write(inline);
            }
            else
            {
                WriteU32(ms, dataOffset);
                var padded = e.Data.Length + (e.Data.Length & 1);
                dataOffset += (uint)padded;
                blobs.Add(e.Data.Length == padded ? e.Data : [.. e.Data, 0]);
            }
        }

        WriteU32(ms, 0);
        foreach (var blob in blobs)
        {
            ms.Write(blob);
        }
    }

    private static void WriteU16(MemoryStream ms, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        ms.Write(b);
    }

    private static void WriteU32(MemoryStream ms, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        ms.Write(b);
    }
}

/// <summary>Locates the ExifTool script for optional tests (env var PHOTO_CLIPPY_EXIFTOOL).</summary>
internal static class ExifToolLocator
{
    public const string EnvironmentVariable = "PHOTO_CLIPPY_EXIFTOOL";

    public static string? ExecutablePath { get; } = Find();

    /// <summary>The "t/images" folder of an Image-ExifTool distribution, when the env var points into one.</summary>
    public static string? SampleImages =>
        ExecutablePath is { } exe && System.IO.Path.GetDirectoryName(exe) is { } dir
        && Directory.Exists(System.IO.Path.Combine(dir, "t", "images"))
            ? System.IO.Path.Combine(dir, "t", "images")
            : null;

    private static string? Find()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
    }
}

/// <summary>Fact that is skipped when ExifTool is not configured through PHOTO_CLIPPY_EXIFTOOL.</summary>
internal sealed class ExifToolFactAttribute : FactAttribute
{
    public ExifToolFactAttribute()
    {
        if (ExifToolLocator.ExecutablePath is null)
        {
            Skip = "exiftool not available (set " + ExifToolLocator.EnvironmentVariable + ")";
        }
    }
}

/// <summary>Fact that is skipped unless the ExifTool distribution (with its t/images samples) is configured.</summary>
internal sealed class ExifToolSamplesFactAttribute : FactAttribute
{
    public ExifToolSamplesFactAttribute()
    {
        if (ExifToolLocator.SampleImages is null)
        {
            Skip = "ExifTool sample images not available (set " + ExifToolLocator.EnvironmentVariable + " to an Image-ExifTool checkout)";
        }
    }
}
