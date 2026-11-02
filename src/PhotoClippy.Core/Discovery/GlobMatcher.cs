using System.Text;
using System.Text.RegularExpressions;

namespace PhotoClippy.Core.Discovery;

/// <summary>
/// Gitignore-like globs: <c>*</c>, <c>?</c>, <c>**</c> (any number of directories). A pattern without '/'
/// matches a file or directory name at any depth. Matching is case-insensitive and uses '/' separators.
/// </summary>
public sealed class GlobMatcher
{
    private readonly Regex[] _patterns;

    public GlobMatcher(IEnumerable<string> patterns)
    {
        _patterns = [.. patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Compile)];
    }

    public bool IsEmpty => _patterns.Length == 0;

    public bool IsMatch(string relativePath)
    {
        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(relativePath))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex Compile(string glob)
    {
        var pattern = glob.Trim().Replace('\\', '/');
        var anchored = pattern.Contains('/', StringComparison.Ordinal);
        pattern = pattern.Trim('/');
        var sb = new StringBuilder(anchored ? "^" : "(^|/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        i++;
                        sb.Append("(.*/)?");
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append("(/|$)");
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
