using PhotoClippy.Testing;

namespace PhotoClippy.Imaging.Tests;

public sealed class RobustnessTests : IDisposable
{
    private readonly TestDirectory directory = new();

    [Theory]
    [InlineData("noise.jpg")]
    [InlineData("noise.png")]
    [InlineData("noise.tif")]
    [InlineData("noise.nef")]
    [InlineData("noise.raf")]
    [InlineData("noise.cr3")]
    public void RandomBytesFail(string name)
    {
        var bytes = new byte[4096];
        new Random(name.Length).NextBytes(bytes);
        var outcome = Decoding.Run(directory.Write(name, bytes), 256);
        Assert.Equal(DecodeStatus.Failed, outcome.Status);
        Assert.Null(outcome.Frame);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    [Theory]
    [InlineData("empty.jpg")]
    [InlineData("empty.tif")]
    [InlineData("empty.dng")]
    public void EmptyFilesFail(string name)
    {
        var outcome = Decoding.Run(directory.Write(name, []), 256);
        Assert.Equal(DecodeStatus.Failed, outcome.Status);
        Assert.Equal("empty file", outcome.Error);
    }

    [Fact]
    public void MissingFileFails()
    {
        var outcome = Decoding.Run(Path.Combine(directory.Root, "missing.jpg"), 256);
        Assert.Equal(DecodeStatus.Failed, outcome.Status);
        Assert.Equal("file not found", outcome.Error);

        var missingDirectory = Decoding.Run(Path.Combine(directory.Root, "nope", "missing.nef"), 256);
        Assert.Equal(DecodeStatus.Failed, missingDirectory.Status);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(200)]
    [InlineData(2000)]
    public void TruncatedJpegFailsOrDecodesPartially(int keep)
    {
        var jpeg = TestImages.MarkerImage(800, 600);
        var outcome = Decoding.Run(directory.Write($"truncated-{keep}.jpg", jpeg[..keep]), 256);
        if (keep < 1000)
        {
            Assert.Equal(DecodeStatus.Failed, outcome.Status);
        }

        using var frame = outcome.Frame;
        Assert.Equal(outcome.Status == DecodeStatus.Ok, frame is not null);
    }

    [Fact]
    public void TruncatedRawPreviewFails()
    {
        var raw = ExifWriter.BuildTiff(new ExifSpec { Orientation = 1 }, TestImages.MarkerImage(600, 400));
        var outcome = Decoding.Run(directory.Write("cut.nef", raw[..300]), 256);
        Assert.Equal(DecodeStatus.Failed, outcome.Status);
    }

    [Fact]
    public void HeifAndVideoAreUnsupported()
    {
        byte[] ftyp = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c', 0, 0, 0, 0, (byte)'m', (byte)'i', (byte)'f', (byte)'1', (byte)'h', (byte)'e', (byte)'i', (byte)'c'];
        var heic = Decoding.Run(directory.Write("photo.heic", [.. ftyp, .. new byte[512]]), 256);
        Assert.Equal(DecodeStatus.Unsupported, heic.Status);
        Assert.False(string.IsNullOrEmpty(heic.Error));

        byte[] mp4Head = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1'];
        var mp4 = Decoding.Run(directory.Write("clip.mp4", [.. mp4Head, .. new byte[512]]), 256);
        Assert.Equal(DecodeStatus.Unsupported, mp4.Status);

        var mov = Decoding.Run(Path.Combine(directory.Root, "absent.MOV"), 256);
        Assert.Equal(DecodeStatus.Unsupported, mov.Status);

        var text = Decoding.Run(directory.Write("notes.txt", "hello world"u8.ToArray()), 256);
        Assert.Equal(DecodeStatus.Unsupported, text.Status);
    }

    [Fact]
    public void JpegWithHeicExtensionStillDecodes()
    {
        using var decoded = Decoding.Ok(directory.Write("mislabelled.heic", TestImages.MarkerImage(300, 200)), 150);
        Assert.Equal("jpeg", decoded.Method);
    }

    [Theory]
    [InlineData("a.jpg", true)]
    [InlineData("A.JPEG", true)]
    [InlineData("b.png", true)]
    [InlineData("c.webp", true)]
    [InlineData("d.gif", true)]
    [InlineData("e.tif", true)]
    [InlineData("f.TIFF", true)]
    [InlineData("g.NEF", true)]
    [InlineData("h.arw", true)]
    [InlineData("i.cr2", true)]
    [InlineData("j.cr3", true)]
    [InlineData("k.raf", true)]
    [InlineData("l.rw2", true)]
    [InlineData("m.dng", true)]
    [InlineData("n.orf", true)]
    [InlineData("o.mp4", false)]
    [InlineData("p.mov", false)]
    [InlineData("q.txt", false)]
    [InlineData("r.heic", false)]
    [InlineData("s.avif", false)]
    [InlineData("noextension", false)]
    [InlineData("", false)]
    public void IsSupportedReflectsFormats(string path, bool expected)
        => Assert.Equal(expected, Decoding.Decoder.IsSupported(path));

    [Fact]
    public void InvalidRequestsThrow()
    {
        var decoder = new FrameDecoder();
        Assert.Throws<ArgumentNullException>(() => decoder.Decode(null!));
        Assert.ThrowsAny<ArgumentException>(() => decoder.Decode(new DecodeRequest { Path = "", MaxEdge = 10 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Decode(new DecodeRequest { Path = "x.jpg", MaxEdge = 0 }));
    }

    [Fact]
    public void CancelledTokenThrows()
    {
        var path = directory.Write("cancel.jpg", TestImages.MarkerImage(200, 100));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(
            () => Decoding.Decoder.Decode(new DecodeRequest { Path = path, MaxEdge = 64 }, cts.Token));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var decoded = Decoding.Ok(directory.Write("dispose.jpg", TestImages.MarkerImage(320, 200)), 160);
        decoded.Dispose();
        decoded.Dispose();
        Assert.Equal(160, decoded.Frame.Width);
    }

    [Fact]
    public void ConcurrentDecodesMatchSequentialResults()
    {
        var files = new List<(string Path, int MaxEdge, int? Orientation)>();
        for (var i = 0; i < 8; i++)
        {
            var orientation = (i % 8) + 1;
            var jpeg = ExifWriter.InjectIntoJpeg(TestImages.MarkerImage(640 + (i * 16), 480), new ExifSpec { Orientation = orientation });
            files.Add((directory.Write($"c{i}.jpg", jpeg), 128 + (i * 8), null));
        }

        files.Add((directory.Write("c-raw.nef", ExifWriter.BuildTiff(new ExifSpec { Orientation = 6 }, TestImages.MarkerImage(600, 400))), 200, null));
        files.Add((directory.Write("c-png.png", TestImages.MarkerImage(300, 200, SkiaSharp.SKEncodedImageFormat.Png)), 100, 3));
        files.Add((directory.Write("c-tiff.tif", TiffWriter.Write(new TiffImage
        {
            Width = 300,
            Height = 200,
            Samples = 3,
            BitsPerSample = 8,
            Data = RawPreviewTests.MarkerData(300, 200),
            Compression = TiffCompression.Lzw,
            Predictor = true,
        })), 150, null));
        files.Add((directory.Write("c-bad.jpg", [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]), 100, null));

        var expected = new long[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            expected[i] = Fingerprint(files[i]);
        }

        var actual = new long[64];
        Parallel.For(0, actual.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => actual[i] = Fingerprint(files[i % files.Count]));

        for (var i = 0; i < actual.Length; i++)
        {
            Assert.Equal(expected[i % files.Count], actual[i]);
        }
    }

    private static long Fingerprint((string Path, int MaxEdge, int? Orientation) file)
    {
        var outcome = Decoding.Run(file.Path, file.MaxEdge, file.Orientation);
        using var frame = outcome.Frame;
        return frame is null ? -(long)outcome.Status - 1 : TestImages.Checksum(frame.Frame);
    }

    public void Dispose() => directory.Dispose();
}
