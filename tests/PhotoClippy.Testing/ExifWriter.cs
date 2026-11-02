using System.Buffers.Binary;
using System.Text;

namespace PhotoClippy.Testing;

/// <summary>Values to embed in a synthetic EXIF block. Null fields are omitted.</summary>
public sealed record ExifSpec
{
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? Software { get; init; }
    public string? BodySerial { get; init; }
    public string? LensMake { get; init; }
    public string? LensModel { get; init; }
    public (double MinFocal, double MaxFocal, double MinFAtMin, double MinFAtMax)? LensSpec { get; init; }
    public double? FocalLength { get; init; }
    public int? FocalLength35 { get; init; }
    public double? FNumber { get; init; }
    public double? ExposureTime { get; init; }
    public int? Iso { get; init; }
    public double? ExposureBias { get; init; }
    public int? ExposureProgram { get; init; }
    public int? Flash { get; init; }
    public DateTime? DateTimeOriginal { get; init; }
    public string? SubSec { get; init; }
    public string? OffsetTime { get; init; }
    public string? ImageUniqueId { get; init; }
    public int? Orientation { get; init; }
    public int? PixelWidth { get; init; }
    public int? PixelHeight { get; init; }
    public double? FocalPlaneXResolutionPerMm { get; init; }
    public (double Lat, double Lon)? Gps { get; init; }
    public int? Rating { get; init; }
}

/// <summary>Minimal little-endian TIFF/EXIF writer for deterministic test fixtures.</summary>
public static class ExifWriter
{
    private const ushort Ascii = 2, Short = 3, Long = 4, Rational = 5, SRational = 10;

    private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

    /// <summary>Returns a TIFF stream (header + IFD0 + EXIF IFD + GPS IFD) and optionally a trailing blob referenced from IFD0.</summary>
    public static byte[] BuildTiff(ExifSpec spec, byte[]? previewJpeg = null)
    {
        var ifd0 = new List<Entry>();
        var exif = new List<Entry>();
        var gps = new List<Entry>();

        AddAscii(ifd0, 0x010F, spec.Make);
        AddAscii(ifd0, 0x0110, spec.Model);
        if (spec.Orientation is { } o)
        {
            ifd0.Add(ShortEntry(0x0112, (ushort)o));
        }

        AddAscii(ifd0, 0x0131, spec.Software);
        if (spec.Rating is { } rating)
        {
            ifd0.Add(ShortEntry(0x4746, (ushort)rating));
        }

        if (spec.ExposureTime is { } t)
        {
            exif.Add(RationalEntry(0x829A, t));
        }

        if (spec.FNumber is { } f)
        {
            exif.Add(RationalEntry(0x829D, f));
        }

        if (spec.ExposureProgram is { } ep)
        {
            exif.Add(ShortEntry(0x8822, (ushort)ep));
        }

        if (spec.Iso is { } iso)
        {
            exif.Add(ShortEntry(0x8827, (ushort)Math.Min(iso, 65535)));
        }

        if (spec.DateTimeOriginal is { } dt)
        {
            AddAscii(exif, 0x9003, dt.ToString("yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
        }

        AddAscii(exif, 0x9011, spec.OffsetTime);
        if (spec.ExposureBias is { } eb)
        {
            exif.Add(SRationalEntry(0x9204, eb));
        }

        if (spec.Flash is { } flash)
        {
            exif.Add(ShortEntry(0x9209, (ushort)flash));
        }

        if (spec.FocalLength is { } fl)
        {
            exif.Add(RationalEntry(0x920A, fl));
        }

        AddAscii(exif, 0x9291, spec.SubSec);
        if (spec.PixelWidth is { } pw)
        {
            exif.Add(LongEntry(0xA002, (uint)pw));
        }

        if (spec.PixelHeight is { } ph)
        {
            exif.Add(LongEntry(0xA003, (uint)ph));
        }

        if (spec.FocalPlaneXResolutionPerMm is { } fpr)
        {
            exif.Add(RationalEntry(0xA20E, fpr * 10));
            exif.Add(ShortEntry(0xA210, 3));
        }

        if (spec.FocalLength35 is { } f35)
        {
            exif.Add(ShortEntry(0xA405, (ushort)f35));
        }

        AddAscii(exif, 0xA420, spec.ImageUniqueId);
        AddAscii(exif, 0xA431, spec.BodySerial);
        if (spec.LensSpec is { } ls)
        {
            exif.Add(new Entry(0xA432, Rational, 4, [.. Rat(ls.MinFocal), .. Rat(ls.MaxFocal), .. Rat(ls.MinFAtMin), .. Rat(ls.MinFAtMax)]));
        }

        AddAscii(exif, 0xA433, spec.LensMake);
        AddAscii(exif, 0xA434, spec.LensModel);

        if (spec.Gps is { } g)
        {
            gps.Add(new Entry(0x0000, 1, 4, [2, 3, 0, 0]));
            AddAscii(gps, 0x0001, g.Lat >= 0 ? "N" : "S");
            gps.Add(new Entry(0x0002, Rational, 3, Dms(Math.Abs(g.Lat))));
            AddAscii(gps, 0x0003, g.Lon >= 0 ? "E" : "W");
            gps.Add(new Entry(0x0004, Rational, 3, Dms(Math.Abs(g.Lon))));
        }

        // Sizes are needed before offsets can be resolved, so pointer entries are added with placeholders first.
        if (exif.Count > 0)
        {
            ifd0.Add(LongEntry(0x8769, 0));
        }

        if (gps.Count > 0)
        {
            ifd0.Add(LongEntry(0x8825, 0));
        }

        if (previewJpeg is not null)
        {
            ifd0.Add(LongEntry(0x00FE, 1));
            ifd0.Add(ShortEntry(0x0103, 6));
            ifd0.Add(LongEntry(0x0201, 0));
            ifd0.Add(LongEntry(0x0202, (uint)previewJpeg.Length));
        }

        var off0 = 8u;
        var offExif = off0 + BlockSize(ifd0);
        var offGps = offExif + BlockSize(exif);
        var offBlob = offGps + BlockSize(gps);

        Replace(ifd0, 0x8769, offExif);
        Replace(ifd0, 0x8825, offGps);
        Replace(ifd0, 0x0201, offBlob);

        using var ms = new MemoryStream();
        ms.Write("II"u8);
        WriteU16(ms, 42);
        WriteU32(ms, off0);
        WriteIfd(ms, ifd0, off0);
        if (exif.Count > 0)
        {
            WriteIfd(ms, exif, offExif);
        }

        if (gps.Count > 0)
        {
            WriteIfd(ms, gps, offGps);
        }

        if (previewJpeg is not null)
        {
            ms.Write(previewJpeg);
        }

        return ms.ToArray();
    }

    /// <summary>Inserts an EXIF APP1 segment right after the JPEG SOI marker.</summary>
    public static byte[] InjectIntoJpeg(byte[] jpeg, ExifSpec spec)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new ArgumentException("Not a JPEG.", nameof(jpeg));
        }

        var tiff = BuildTiff(spec);
        var segLen = 2 + 6 + tiff.Length;
        if (segLen > 0xFFFF)
        {
            throw new InvalidOperationException("EXIF block too large.");
        }

        var output = new byte[jpeg.Length + 2 + segLen];
        output[0] = 0xFF;
        output[1] = 0xD8;
        output[2] = 0xFF;
        output[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(4), (ushort)segLen);
        "Exif\0\0"u8.CopyTo(output.AsSpan(6));
        tiff.CopyTo(output.AsSpan(12));
        jpeg.AsSpan(2).CopyTo(output.AsSpan(12 + tiff.Length));
        return output;
    }

    private static uint BlockSize(List<Entry> entries)
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

    private static void WriteIfd(MemoryStream ms, List<Entry> entries, uint offset)
    {
        entries.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        var dataOffset = offset + 2u + (12u * (uint)entries.Count) + 4u;
        WriteU16(ms, (ushort)entries.Count);
        var blobs = new List<byte[]>();
        Span<byte> inline = stackalloc byte[4];
        foreach (var e in entries)
        {
            WriteU16(ms, e.Tag);
            WriteU16(ms, e.Type);
            WriteU32(ms, e.Count);
            if (e.Data.Length <= 4)
            {
                inline.Clear();
                e.Data.CopyTo(inline);
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
        foreach (var b in blobs)
        {
            ms.Write(b);
        }
    }

    private static void Replace(List<Entry> entries, ushort tag, uint value)
    {
        var i = entries.FindIndex(e => e.Tag == tag);
        if (i >= 0)
        {
            entries[i] = LongEntry(tag, value);
        }
    }

    private static void AddAscii(List<Entry> list, ushort tag, string? value)
    {
        if (value is null)
        {
            return;
        }

        var bytes = Encoding.ASCII.GetBytes(value + "\0");
        list.Add(new Entry(tag, Ascii, (uint)bytes.Length, bytes));
    }

    private static Entry ShortEntry(ushort tag, ushort value)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        return new Entry(tag, Short, 1, b);
    }

    private static Entry LongEntry(ushort tag, uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return new Entry(tag, Long, 1, b);
    }

    private static Entry RationalEntry(ushort tag, double value) => new(tag, Rational, 1, Rat(value));

    private static Entry SRationalEntry(ushort tag, double value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(b, (int)Math.Round(value * 1000));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 1000);
        return new Entry(tag, SRational, 1, b);
    }

    private static byte[] Rat(double value)
    {
        uint num, den;
        if (value > 0 && value < 1)
        {
            // Exposure times such as 1/250 are stored exactly as 1/x.
            var inv = 1 / value;
            if (Math.Abs(inv - Math.Round(inv)) < 1e-6)
            {
                num = 1;
                den = (uint)Math.Round(inv);
            }
            else
            {
                num = (uint)Math.Round(value * 100000);
                den = 100000;
            }
        }
        else
        {
            num = (uint)Math.Round(value * 100);
            den = 100;
        }

        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b, num);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), den);
        return b;
    }

    private static byte[] Dms(double deg)
    {
        var d = Math.Floor(deg);
        var mFull = (deg - d) * 60;
        var m = Math.Floor(mFull);
        var s = (mFull - m) * 60;
        return [.. Rat(d), .. Rat(m), .. Rat(s)];
    }

    private static void WriteU16(MemoryStream ms, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        ms.Write(b);
    }

    private static void WriteU32(MemoryStream ms, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        ms.Write(b);
    }
}
