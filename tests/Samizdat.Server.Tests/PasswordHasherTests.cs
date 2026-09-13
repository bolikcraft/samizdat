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
}
