using PhotoClippy.Plugins.Auth;

namespace PhotoClippy.Auth.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Hash_round_trips_and_rejects_wrong_passwords()
    {
        var encoded = PasswordHasher.Hash("correct horse", 1_000);

        Assert.StartsWith("pbkdf2-sha256$1000$", encoded, StringComparison.Ordinal);
        Assert.True(PasswordHasher.Verify("correct horse", encoded, 1_000, out var rehash));
        Assert.False(rehash);
        Assert.False(PasswordHasher.Verify("correct horse ", encoded, 1_000, out _));
        Assert.False(PasswordHasher.Verify(string.Empty, encoded, 1_000, out _));
    }

    [Fact]
    public void Each_hash_uses_a_fresh_salt()
    {
        Assert.NotEqual(PasswordHasher.Hash("same", 1_000), PasswordHasher.Hash("same", 1_000));
    }

    [Fact]
    public void Weaker_hashes_ask_to_be_upgraded()
    {
        var encoded = PasswordHasher.Hash("secret-pass", 1_000);

        Assert.True(PasswordHasher.Verify("secret-pass", encoded, 2_000, out var rehash));
        Assert.True(rehash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("pbkdf2-sha256$x$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$1000$not-base64!$AAAA")]
    [InlineData("md5$1000$AAAA$AAAA")]
    public void Malformed_hashes_never_verify(string encoded)
    {
        Assert.False(PasswordHasher.Verify("anything", encoded, 1_000, out _));
    }
}
