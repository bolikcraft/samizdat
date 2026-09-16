using Samizdat.Server.Auth;

namespace Samizdat.Server.Tests;

public class PasswordHasherTests
{
    [Theory]
    [InlineData("argon2id$не-base64$не-base64")]
    [InlineData("argon2id$$")]
    [InlineData("мусор")]
    [InlineData("")]
    [InlineData("argon2id$AA==$не-base64")]
    public void Verify_returns_false_instead_of_throwing_on_corrupted_hash(string stored)
        => Assert.False(PasswordHasher.Verify("любой пароль", stored));

    // Приманка обязана быть настоящим хэшем: на мусоре Verify отвечает сразу, и утечка по времени вернётся.
    [Fact]
    public void Decoy_is_a_well_formed_hash_that_no_password_matches()
    {
        Assert.Matches("^argon2id\\$[A-Za-z0-9+/]{22}==\\$[A-Za-z0-9+/]{43}=$", PasswordHasher.Decoy);
        Assert.False(PasswordHasher.Verify("", PasswordHasher.Decoy));
        Assert.False(PasswordHasher.Verify("admin", PasswordHasher.Decoy));
    }
}
