namespace PhotoClippy.Cli.Web;

public sealed record FolderEntry(string Name, string Path);

public sealed record FolderListing(string Path, string? Parent, IReadOnlyList<FolderEntry> Folders, bool Truncated, string? Error = null);

/// <summary>
/// Directory listing for the library-root picker, confined to the allowed browse roots. Symbolic links below a
/// root are neither listed nor traversed, so a link cannot be used to escape the roots.
/// </summary>
public sealed class FolderBrowser(Func<IReadOnlyList<string>> roots)
{
    private const int MaxEntries = 500;
    private static readonly StringComparison PathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public IReadOnlyList<string> Roots => [.. roots().Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize).Distinct(StringComparer.FromComparison(PathComparison)).Where(Directory.Exists)];

    /// <summary>True when <paramref name="path"/> resolves to a directory inside one of the roots without crossing a symbolic link.</summary>
    public bool IsAllowed(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var root in Roots)
        {
            if (string.Equals(full, root, PathComparison))
            {
                return true;
            }

            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, PathComparison))
            {
                continue;
            }

            var current = root;
            var ok = true;
            foreach (var segment in full[prefix.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                var info = new DirectoryInfo(current);
                if (!info.Exists || info.LinkTarget is not null)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }

    public FolderListing List(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FolderListing(string.Empty, null, [.. Roots.Select(r => new FolderEntry(r, r))], false);
        }

        if (!IsAllowed(path, out var full))
        {
            return new FolderListing(path, null, [], false, "That folder is outside the folders this server may browse.");
        }

        var isRoot = Roots.Any(r => string.Equals(r, full, PathComparison));
        var parent = isRoot ? string.Empty : Path.GetDirectoryName(full);
        try
        {
            var folders = new List<FolderEntry>();
            var truncated = false;
            foreach (var dir in new DirectoryInfo(full).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
            {
                if (dir.Name.StartsWith('.') || dir.Name.StartsWith('@') || dir.LinkTarget is not null)
                {
                    continue;
                }

                if (folders.Count == MaxEntries)
                {
                    truncated = true;
                    break;
                }

                folders.Add(new FolderEntry(dir.Name, dir.FullName));
            }

            folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return new FolderListing(full, parent, folders, truncated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderListing(full, parent, [], false, ex.Message);
        }
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        return full.Length > 1 ? Path.TrimEndingDirectorySeparator(full) : full;
    }
}
