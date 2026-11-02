using System.Collections.Frozen;
using System.IO.Enumeration;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Discovery;

public sealed record DiscoveryOptions
{
    public required IReadOnlyList<string> Roots { get; init; }
    public IReadOnlyList<string> Ignore { get; init; } = [];
    public bool FollowSymlinks { get; init; }
    public bool IncludeHidden { get; init; }
}

/// <summary>Recursively enumerates supported image files without per-file stat calls beyond the directory read.</summary>
public sealed class FileDiscovery(DiscoveryOptions options)
{
    /// <summary>NAS/OS housekeeping folders and files that never contain originals.</summary>
    public static readonly FrozenSet<string> IgnoredNames = new[]
    {
        "@eaDir", "#recycle", "#snapshot", "@Recycle", "@Recently-Snapshot", ".@__thumb", "$RECYCLE.BIN",
        "System Volume Information", ".Trashes", ".Trash", ".Spotlight-V100", ".fseventsd", ".thumbnails",
        ".thumbs", "Thumbs.db", ".DS_Store", "lost+found", ".photo-clippy", ".dtrash", ".picasaoriginals",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly GlobMatcher _ignore = new(options.Ignore);

    public int SkippedDirectories { get; private set; }

    public IEnumerable<SourceFile> Enumerate(CancellationToken cancellationToken = default)
    {
        var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var visitedDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawRoot in options.Roots)
        {
            var root = Path.GetFullPath(rawRoot);
            if (File.Exists(root))
            {
                if (MediaTypes.TryGetKind(Path.GetFileName(root.AsSpan()), out var kind) && seen.Add(root))
                {
                    var info = new FileInfo(root);
                    yield return new SourceFile { Path = root, Size = info.Length, LastWriteUtc = info.LastWriteTimeUtc, Kind = kind };
                }

                continue;
            }

            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Library root not found: {rawRoot}");
            }

            visitedDirs.Add(RealPath(root));
            foreach (var file in EnumerateRoot(root, visitedDirs))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(file.Path))
                {
                    yield return file;
                }
            }
        }
    }

    private IEnumerable<SourceFile> EnumerateRoot(string root, HashSet<string> visitedDirs)
    {
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        var rootLength = root.Length;
        var enumerable = new FileSystemEnumerable<SourceFile?>(root, Transform, enumeration)
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && IncludeFile(ref entry, rootLength),
            ShouldRecursePredicate = (ref FileSystemEntry entry) => ShouldRecurse(ref entry, rootLength, visitedDirs),
        };

        foreach (var file in enumerable)
        {
            if (file is not null)
            {
                yield return file;
            }
        }
    }

    private static SourceFile? Transform(ref FileSystemEntry entry)
    {
        MediaTypes.TryGetKind(entry.FileName, out var kind);
        return new SourceFile
        {
            Path = entry.ToFullPath(),
            Size = entry.Length,
            LastWriteUtc = entry.LastWriteTimeUtc,
            Kind = kind,
        };
    }

    private bool IncludeFile(ref FileSystemEntry entry, int rootLength)
    {
        var name = entry.FileName;
        if (name.StartsWith("._", StringComparison.Ordinal) || !MediaTypes.TryGetKind(name, out _))
        {
            return false;
        }

        if (!options.IncludeHidden && IsHidden(ref entry))
        {
            return false;
        }

        if (entry.Length == 0)
        {
            return false;
        }

        return _ignore.IsEmpty || !_ignore.IsMatch(Relative(ref entry, rootLength));
    }

    private bool ShouldRecurse(ref FileSystemEntry entry, int rootLength, HashSet<string> visitedDirs)
    {
        var name = entry.FileName.ToString();
        if (IgnoredNames.Contains(name) || (!options.IncludeHidden && IsHidden(ref entry)))
        {
            SkippedDirectories++;
            return false;
        }

        if (!_ignore.IsEmpty && _ignore.IsMatch(Relative(ref entry, rootLength)))
        {
            SkippedDirectories++;
            return false;
        }

        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            if (!options.FollowSymlinks)
            {
                SkippedDirectories++;
                return false;
            }

            var real = RealPath(entry.ToFullPath());
            lock (visitedDirs)
            {
                if (!visitedDirs.Add(real))
                {
                    SkippedDirectories++;
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsHidden(ref FileSystemEntry entry) =>
        entry.FileName.StartsWith(".", StringComparison.Ordinal) || (OperatingSystem.IsWindows() && entry.IsHidden);

    private static string Relative(ref FileSystemEntry entry, int rootLength)
    {
        var full = entry.ToFullPath();
        var relative = full.Length > rootLength ? full[rootLength..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : string.Empty;
        return Path.DirectorySeparatorChar == '/' ? relative : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string RealPath(string path)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? path);
        }
        catch (IOException)
        {
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }
    }
}
