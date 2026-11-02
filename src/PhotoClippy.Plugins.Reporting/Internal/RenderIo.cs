using System.Security.Cryptography;
using System.Text;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Reporting;

internal static class RenderIo
{
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string? Option(RenderTarget target, string key)
    {
        if (target.Options is null)
        {
            return null;
        }

        if (target.Options.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var (k, v) in target.Options)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    public static bool? Flag(RenderTarget target, string key) => Option(target, key)?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => null,
    };

    public static string PrepareDirectory(RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.OutputDirectory))
        {
            throw new ArgumentException("RenderTarget.OutputDirectory is required.", nameof(target));
        }

        var dir = Path.GetFullPath(target.OutputDirectory);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string BaseName(RenderTarget target)
    {
        var name = string.IsNullOrWhiteSpace(target.BaseName) ? "photo-report" : target.BaseName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':' ? '-' : c);
        }

        var result = sb.ToString().Trim('.', ' ');
        return result.Length == 0 ? "photo-report" : result;
    }

    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var tmp = path + ".partial";
        try
        {
            await File.WriteAllTextAsync(tmp, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Fingerprint(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
}
