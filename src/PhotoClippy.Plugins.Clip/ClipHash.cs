using System.Security.Cryptography;
using System.Text;

namespace PhotoClippy.Plugins.Clip;

internal static class ClipHash
{
    public static string Hex(string value, int length)
    {
        var hex = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        return hex[..Math.Min(length, hex.Length)];
    }
}
