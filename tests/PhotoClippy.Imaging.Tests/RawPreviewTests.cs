using System.Buffers.Binary;
using System.Text;
using PhotoClippy.Testing;

namespace PhotoClippy.Imaging.Tests;

public sealed class RawPreviewTests : IDisposable
{
    private readonly TestDirectory directory = new();

    [Theory]
    [InlineData("nef")]
    [InlineData("arw")]
    [InlineData("dng")]
    [InlineData("cr2")]
    [InlineData("ORF")]
    public void TiffBasedRawUsesEmbeddedPreview(string extension)
    {
        var preview = TestImages.MarkerImage(600, 400);
        var raw = ExifWriter.BuildTiff(new ExifSpec { Make = "Test", PixelWidth = 6000, PixelHeight = 4000 }, preview);
        var path = directory.Write("preview." + extension, raw);

        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal("raw-preview", decoded.Method);
        Assert.Equal((300, 200), (decoded.Frame.Width, decoded.Frame.Height));
        Assert.Equal((6000, 4000), (decoded.Frame.SourceWidth, decoded.Frame.SourceHeight));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopLeft, Corner.TopRight);
    }

    [Fact]
    public void RawOrientationComesFromIfd0()
    {
        var raw = ExifWriter.BuildTiff(new ExifSpec { Orientation = 6, PixelWidth = 6000, PixelHeight = 4000 }, TestImages.MarkerImage(600, 400));
        var path = directory.Write("rotated.nef", raw);

        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal((200, 300), (decoded.Frame.Width, decoded.Frame.Height));
        Assert.Equal((4000, 6000), (decoded.Frame.SourceWidth, decoded.Frame.SourceHeight));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopRight, Corner.BottomRight);
    }

    [Fact]
    public void RequestOrientationOverridesRawIfd0()
    {
        var raw = ExifWriter.BuildTiff(new ExifSpec { Orientation = 6 }, TestImages.MarkerImage(600, 400));
        var path = directory.Write("override.arw", raw);

        using var decoded = Decoding.Ok(path, 300, orientation: 3);
        Assert.Equal((300, 200), (decoded.Frame.Width, decoded.Frame.Height));
        Assert.Equal((600, 400), (decoded.Frame.SourceWidth, decoded.Frame.SourceHeight));
        TestImages.AssertMarkers(decoded.Frame, Corner.BottomRight, Corner.BottomLeft);
    }

    [Fact]
    public void RawWithoutOrientationFallsBackToPreviewExif()
    {
        var preview = ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(600, 400), new ExifSpec { Orientation = 8 });
        var path = directory.Write("preview-exif.dng", ExifWriter.BuildTiff(new ExifSpec { Make = "Test" }, preview));

        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal((200, 300), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.BottomLeft, Corner.TopLeft);
    }

    [Fact]
    public void SmallestSufficientPreviewIsChosen()
    {
        var thumbnail = TestImages.SolidJpeg(320, 213, new Rgb(20, 40, 220));
        var main = MarkerData(1200, 800);
        var tiff = TiffWriter.Write(new TiffImage
        {
            Width = 1200,
            Height = 800,
            Samples = 3,
            BitsPerSample = 8,
            Data = main,
            Photometric = 6,
            Compression = TiffCompression.Jpeg,
            RowsPerStrip = 800,
            PreviewJpeg = thumbnail,
        });
        var path = directory.Write("two-previews.dng", tiff);

        using (var small = Decoding.Ok(path, 256))
        {
            Assert.Equal("raw-preview", small.Method);
            Assert.Equal((256, 170), (small.Frame.Width, small.Frame.Height));
            Assert.Equal((1200, 800), (small.Frame.SourceWidth, small.Frame.SourceHeight));
            Assert.True(TestImages.Mean(small.Frame, 0.4, 0.4, 0.6, 0.6).B > 180);
        }

        using (var large = Decoding.Ok(path, 600))
        {
            Assert.Equal((600, 400), (large.Frame.Width, large.Frame.Height));
            TestImages.AssertMarkers(large.Frame, Corner.TopLeft, Corner.TopRight);
        }

        using (var full = Decoding.Ok(path, 5000))
        {
            Assert.Equal((1200, 800), (full.Frame.Width, full.Frame.Height));
            TestImages.AssertMarkers(full.Frame, Corner.TopLeft, Corner.TopRight);
        }
    }

    [Fact]
    public void RawWithoutJpegFallsBackToUncompressedTiffData()
    {
        var tiff = TiffWriter.Write(new TiffImage { Width = 300, Height = 200, Samples = 3, BitsPerSample = 8, Data = MarkerData(300, 200) });
        var path = directory.Write("linear.dng", tiff);

        using var decoded = Decoding.Ok(path, 150);
        Assert.Equal("raw-preview", decoded.Method);
        Assert.Equal((150, 100), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopLeft, Corner.TopRight);
    }

    [Fact]
    public void BigEndianRawIsParsed()
    {
        var tiff = TiffWriter.Write(new TiffImage
        {
            Width = 16,
            Height = 16,
            Samples = 1,
            BitsPerSample = 8,
            Data = new ushort[256],
            Photometric = 1,
            BigEndian = true,
            Orientation = 6,
            PreviewJpeg = TestImages.MarkerImage(600, 400),
        });
        var path = directory.Write("motorola.pef", tiff);

        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal("raw-preview", decoded.Method);
        Assert.Equal((200, 300), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopRight, Corner.BottomRight);
    }

    [Fact]
    public void FujifilmRafUsesHeaderPreview()
    {
        var jpeg = ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(600, 400), new ExifSpec { Orientation = 3 });
        var raf = new byte[160 + jpeg.Length + 4096];
        Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW 0201FF383501").CopyTo(raf, 0);
        BinaryPrimitives.WriteUInt32BigEndian(raf.AsSpan(84), 160);
        BinaryPrimitives.WriteUInt32BigEndian(raf.AsSpan(88), (uint)jpeg.Length);
        jpeg.CopyTo(raf, 160);
        var path = directory.Write("fuji.raf", raf);

        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal("raw-preview", decoded.Method);
        Assert.Equal((300, 200), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.BottomRight, Corner.BottomLeft);
    }

    [Fact]
    public void PanasonicRw2UsesJpgFromRaw()
    {
        var jpeg = TestImages.MarkerImage(600, 400);
        var rw2 = new List<byte>();
        rw2.AddRange("IIU\0"u8.ToArray());
        rw2.AddRange(U32(8));
        rw2.AddRange(U16(2));
        var dataOffset = 8 + 2 + (2 * 12) + 4;
        rw2.AddRange(Entry(0x002E, 7, (uint)jpeg.Length, (uint)dataOffset));
        rw2.AddRange(Entry(0x0112, 3, 1, 8));
        rw2.AddRange(U32(0));
        rw2.AddRange(jpeg);
        var path = directory.Write("panasonic.rw2", [.. rw2]);

        using var decoded = Decoding.Ok(path, 300);
        Assert.Equal("raw-preview", decoded.Method);
        Assert.Equal((200, 300), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.BottomLeft, Corner.TopLeft);
    }

    [Fact]
    public void CanonCr3UsesPreviewAndFullSizeJpegs()
    {
        var path = directory.Write("canon.cr3", BuildCr3(orientation: 6));

        using (var small = Decoding.Ok(path, 256))
        {
            Assert.Equal("raw-preview", small.Method);
            Assert.Equal((171, 256), (small.Frame.Width, small.Frame.Height));
            Assert.Equal((800, 1200), (small.Frame.SourceWidth, small.Frame.SourceHeight));
            Assert.True(TestImages.Mean(small.Frame, 0.4, 0.4, 0.6, 0.6).B > 180, "PRVW (blue) preview expected");
        }

        using (var large = Decoding.Ok(path, 1000))
        {
            Assert.Equal((667, 1000), (large.Frame.Width, large.Frame.Height));
            TestImages.AssertMarkers(large.Frame, Corner.TopRight, Corner.BottomRight);
        }

        using (var thumb = Decoding.Ok(path, 100, orientation: 1))
        {
            Assert.Equal((100, 67), (thumb.Frame.Width, thumb.Frame.Height));
        }
    }

    [Fact]
    public void UnknownRawLayoutFallsBackToByteScan()
    {
        var jpeg = TestImages.MarkerImage(640, 480);
        var junk = new byte[70_000];
        new Random(7).NextBytes(junk);
        junk[1000] = 0xFF;
        junk[1001] = 0xD8;
        junk[1002] = 0xFF;
        var crw = new byte[junk.Length + jpeg.Length + 5000];
        junk.CopyTo(crw, 0);
        jpeg.CopyTo(crw, junk.Length);
        var path = directory.Write("old.crw", crw);

        using var decoded = Decoding.Ok(path, 320);
        Assert.Equal("raw-preview", decoded.Method);
        Assert.Equal((320, 240), (decoded.Frame.Width, decoded.Frame.Height));
        TestImages.AssertMarkers(decoded.Frame, Corner.TopLeft, Corner.TopRight);
    }

    [Fact]
    public void TiffRawWithUnreferencedPreviewFallsBackToByteScan()
    {
        var header = ExifWriter.BuildTiff(new ExifSpec { Make = "Test", Orientation = 2 });
        var jpeg = TestImages.MarkerImage(640, 480);
        var raw = new byte[header.Length + 300 + jpeg.Length];
        header.CopyTo(raw, 0);
        jpeg.CopyTo(raw, header.Length + 300);
        var path = directory.Write("makernote.nef", raw);

        using var decoded = Decoding.Ok(path, 320);
        Assert.Equal("raw-preview", decoded.Method);
        TestImages.AssertMarkers(decoded.Frame, Corner.TopRight, Corner.TopLeft);
    }

    [Fact]
    public void RawWithoutAnyPreviewFails()
    {
        var path = directory.Write("empty.nef", ExifWriter.BuildTiff(new ExifSpec { Make = "Test" }));
        var outcome = Decoding.Run(path, 256);
        Assert.Equal(DecodeStatus.Failed, outcome.Status);
        Assert.NotNull(outcome.Error);
    }

    [DirectoryFact("PHOTO_CLIPPY_RAW_SAMPLES")]
    public void RealRawSamplesDecode()
    {
        var root = Environment.GetEnvironmentVariable("PHOTO_CLIPPY_RAW_SAMPLES")!;
        var decoder = new FrameDecoder();
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(root))
        {
            if (!decoder.IsSupported(path))
            {
                continue;
            }

            using var decoded = Decoding.Ok(path, 512);
            Assert.Equal(512, Math.Max(decoded.Frame.Width, decoded.Frame.Height));
            count++;
        }

        Assert.True(count > 0, "no supported samples found");
    }

    internal static ushort[] MarkerData(int width, int height)
    {
        var data = new ushort[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = TestImages.Marker(x, y, width, height);
                var i = ((y * width) + x) * 3;
                data[i] = (ushort)color.R;
                data[i + 1] = (ushort)color.G;
                data[i + 2] = (ushort)color.B;
            }
        }

        return data;
    }

    private static byte[] BuildCr3(int orientation)
    {
        var full = TestImages.MarkerImage(1200, 800);
        var prvw = TestImages.SolidJpeg(480, 320, new Rgb(20, 40, 220));
        var thumb = TestImages.SolidJpeg(160, 120, new Rgb(220, 220, 20));

        var cmt1 = Box("CMT1", ExifWriter.BuildTiff(new ExifSpec { Make = "Canon", Orientation = orientation }));
        var thmbPayload = Concat(U32Be(0), U16Be(160), U16Be(120), U32Be((uint)thumb.Length), U32Be(0), thumb);
        var canon = Box("uuid", Concat(Cr3Uuid, Box("CNCV", Encoding.ASCII.GetBytes("CanonCR3_001/00.10.00/00.00.00")), cmt1, Box("THMB", thmbPayload)));

        var ftyp = Box("ftyp", Concat(Encoding.ASCII.GetBytes("crx "), U32Be(1), Encoding.ASCII.GetBytes("crx isom")));
        var previewBox = Box("uuid", Concat(PreviewUuid, new byte[8], Box("PRVW", Concat(new byte[16], prvw))));

        byte[] Moov(uint mdatOffset)
        {
            var stsz = Box("stsz", Concat(U32Be(0), U32Be(0), U32Be(1), U32Be((uint)full.Length)));
            var co64 = Box("co64", Concat(U32Be(0), U32Be(1), U32Be(0), U32Be(mdatOffset)));
            var stbl = Box("stbl", Concat(Box("stsd", new byte[16]), stsz, co64));
            var trak = Box("trak", Box("mdia", Concat(Box("mdhd", new byte[24]), Box("minf", stbl))));
            return Box("moov", Concat(canon, trak));
        }

        var prefix = ftyp.Length + Moov(0).Length + previewBox.Length;
        var mdatPayloadOffset = (uint)(prefix + 8);
        return Concat(ftyp, Moov(mdatPayloadOffset), previewBox, Box("mdat", full));
    }

    private static readonly byte[] Cr3Uuid =
        [0x85, 0xC0, 0xB6, 0x87, 0x82, 0x0F, 0x11, 0xE0, 0x81, 0x11, 0xF4, 0xCE, 0x46, 0x2B, 0x6A, 0x48];

    private static readonly byte[] PreviewUuid =
        [0xEA, 0xF4, 0x2B, 0x5E, 0x1C, 0x98, 0x4B, 0x88, 0xB9, 0xFB, 0xB7, 0xDC, 0x40, 0x6E, 0x4D, 0x16];

    private static byte[] Box(string type, ReadOnlySpan<byte> payload)
    {
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box.AsSpan(8));
        return box;
    }

    private static byte[] Concat(params ReadOnlySpan<byte[]> parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] U32Be(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U16Be(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Entry(ushort tag, ushort type, uint count, uint value)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value);
        return bytes;
    }

    public void Dispose() => directory.Dispose();
}
