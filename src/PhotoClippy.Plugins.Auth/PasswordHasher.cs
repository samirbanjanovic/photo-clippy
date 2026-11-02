using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PhotoClippy.Plugins.Auth;

/// <summary>PBKDF2-HMAC-SHA256 password hashes encoded as <c>pbkdf2-sha256$iterations$salt$hash</c> (base64).</summary>
internal static class PasswordHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <param name="needsRehash">True when the hash is valid but weaker than <paramref name="targetIterations"/>.</param>
    public static bool Verify(string password, string encoded, int targetIterations, out bool needsRehash)
    {
        needsRehash = false;
        var parts = encoded.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        var ok = CryptographicOperations.FixedTimeEquals(actual, expected);
        needsRehash = ok && iterations < targetIterations;
        return ok;
    }
}
