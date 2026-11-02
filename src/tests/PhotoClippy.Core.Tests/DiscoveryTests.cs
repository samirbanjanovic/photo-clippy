using PhotoClippy.Contracts;
using PhotoClippy.Core.Discovery;

namespace PhotoClippy.Core.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("*.tmp", "a/b/x.TMP", true)]
    [InlineData("*.tmp", "a/b/x.jpg", false)]
    [InlineData("raw/**", "raw/x/y.jpg", true)]
    [InlineData("raw/**", "other/raw/y.jpg", false)]
    [InlineData("exports", "a/exports/x.jpg", true)]
    [InlineData("exports", "a/exports", true)]
    [InlineData("exports", "a/my-exports/x.jpg", false)]
    [InlineData("a/*.jpg", "a/x.jpg", true)]
    [InlineData("a/*.jpg", "b/a/x.jpg", false)]
    [InlineData("a/*.jpg", "a/b/x.jpg", false)]
    [InlineData("**/thumbs", "x/y/thumbs/z.jpg", true)]
    [InlineData("**/thumbs", "thumbs", true)]
    [InlineData("img_?.jpg", "img_1.jpg", true)]
    [InlineData("img_?.jpg", "img_12.jpg", false)]
    [InlineData("Exports\\Old", "exports/old/a.jpg", true)]
    public void Matches_gitignore_style_patterns(string pattern, string path, bool expected)
    {
        Assert.Equal(expected, new GlobMatcher([pattern]).IsMatch(path));
    }

    [Fact]
    public void Blank_patterns_are_dropped()
    {
        var matcher = new GlobMatcher(["", "  ", null!]);
        Assert.True(matcher.IsEmpty);
        Assert.False(matcher.IsMatch("anything.jpg"));
    }

    [Fact]
    public void Any_of_several_patterns_matches()
    {
        var matcher = new GlobMatcher(["*.png", "tmp"]);
        Assert.False(matcher.IsEmpty);
        Assert.True(matcher.IsMatch("x/a.png"));
        Assert.True(matcher.IsMatch("tmp/a.jpg"));
        Assert.False(matcher.IsMatch("x/a.jpg"));
    }
}

public sealed class MediaTypesTests
{
    [Theory]
    [InlineData("a.jpg", MediaKind.Jpeg)]
    [InlineData("A.JPEG", MediaKind.Jpeg)]
    [InlineData("a.heic", MediaKind.Heif)]
    [InlineData("a.avif", MediaKind.Heif)]
    [InlineData("a.tif", MediaKind.Tiff)]
    [InlineData("a.png", MediaKind.Png)]
    [InlineData("a.webp", MediaKind.Other)]
    [InlineData("a.NEF", MediaKind.Raw)]
    [InlineData("a.cr3", MediaKind.Raw)]
    [InlineData("archive.v2.dng", MediaKind.Raw)]
    public void Recognises_supported_extensions(string name, MediaKind expected)
    {
        Assert.True(MediaTypes.TryGetKind(name, out var kind));
        Assert.Equal(expected, kind);
        Assert.True(MediaTypes.IsSupported(Path.Combine("dir", name)));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("a.xmp")]
    [InlineData("a.mov")]
    [InlineData("noextension")]
    public void Rejects_other_files(string name)
    {
        Assert.False(MediaTypes.TryGetKind(name, out _));
        Assert.False(MediaTypes.IsSupported(name));
    }
}

public sealed class FileDiscoveryTests : IDisposable
{
    private static readonly byte[] Payload = [1, 2, 3];
    private readonly TestDir _dir = new("discovery");

    public void Dispose() => _dir.Dispose();

    private List<SourceFile> Enumerate(IReadOnlyList<string>? ignore = null, bool includeHidden = false, params string[] roots)
    {
        var discovery = new FileDiscovery(new DiscoveryOptions
        {
            Roots = roots.Length == 0 ? [_dir.Path] : roots,
            Ignore = ignore ?? [],
            IncludeHidden = includeHidden,
        });
        return [.. discovery.Enumerate()];
    }

    private List<string> Relative(IEnumerable<SourceFile> files) =>
        [.. files.Select(f => Path.GetRelativePath(_dir.Path, f.Path).Replace('\\', '/')).Order(StringComparer.Ordinal)];

    [Fact]
    public void Recurses_and_filters_by_media_type()
    {
        _dir.WriteBytes("a.jpg", Payload);
        _dir.WriteBytes("2023/b.NEF", Payload);
        _dir.WriteBytes("2023/deep/c.heic", Payload);
        _dir.WriteBytes("2023/c.xmp", Payload);
        _dir.WriteBytes("readme.txt", Payload);

        var files = Enumerate();

        Assert.Equal(["2023/b.NEF", "2023/deep/c.heic", "a.jpg"], Relative(files));
        Assert.Equal(MediaKind.Raw, files.Single(f => f.Path.EndsWith(".NEF", StringComparison.Ordinal)).Kind);
        Assert.All(files, f => Assert.Equal(Payload.Length, f.Size));
    }

    [Fact]
    public void Skips_housekeeping_hidden_resource_fork_and_empty_files()
    {
        _dir.WriteBytes("keep.jpg", Payload);
        _dir.WriteBytes("@eaDir/keep.jpg/SYNOPHOTO_THUMB.jpg", Payload);
        _dir.WriteBytes(".hidden/x.jpg", Payload);
        _dir.WriteBytes(".secret.jpg", Payload);
        _dir.WriteBytes("._keep.jpg", Payload);
        _dir.WriteBytes("empty.jpg", []);

        var discovery = new FileDiscovery(new DiscoveryOptions { Roots = [_dir.Path] });
        var files = discovery.Enumerate().ToList();

        Assert.Equal(["keep.jpg"], Relative(files));
        Assert.Equal(2, discovery.SkippedDirectories);
    }

    [Fact]
    public void Include_hidden_picks_up_dot_files_but_not_housekeeping()
    {
        _dir.WriteBytes(".hidden/x.jpg", Payload);
        _dir.WriteBytes(".DS_Store/y.jpg", Payload);

        Assert.Equal([".hidden/x.jpg"], Relative(Enumerate(includeHidden: true)));
    }

    [Fact]
    public void Ignore_globs_apply_to_files_and_directories()
    {
        _dir.WriteBytes("keep/a.jpg", Payload);
        _dir.WriteBytes("exports/b.jpg", Payload);
        _dir.WriteBytes("keep/sub/exports/c.jpg", Payload);
        _dir.WriteBytes("keep/d.png", Payload);
        _dir.WriteBytes("raw/e.nef", Payload);

        var files = Enumerate(ignore: ["exports", "*.png", "raw/**"]);

        Assert.Equal(["keep/a.jpg"], Relative(files));
    }

    [Fact]
    public void File_roots_are_yielded_and_duplicates_removed()
    {
        var file = _dir.WriteBytes("single.jpg", Payload);
        _dir.WriteBytes("notes.txt", Payload);

        var files = Enumerate(null, false, file, _dir.Path, _dir.Path, _dir.Combine("notes.txt"));

        Assert.Single(files);
        Assert.Equal(Path.GetFullPath(file), files[0].Path);
    }

    [Fact]
    public void Missing_root_throws_a_clear_error()
    {
        var missing = _dir.Combine("nope");
        var ex = Assert.Throws<DirectoryNotFoundException>(() => Enumerate(null, false, missing));
        Assert.Contains("Library root not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Honours_cancellation()
    {
        _dir.WriteBytes("a.jpg", Payload);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var discovery = new FileDiscovery(new DiscoveryOptions { Roots = [_dir.Path] });
        Assert.Throws<OperationCanceledException>(() => discovery.Enumerate(cts.Token).ToList());
    }
}
