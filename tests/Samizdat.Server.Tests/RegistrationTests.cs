using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class RegistrationTests
{
    [Fact]
    public void Invite_is_alive_until_it_is_used_revoked_or_expired()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(new InviteRow { Token = "t" }.IsAlive(now));
        Assert.True(new InviteRow { Token = "t", ExpiresAt = now.AddDays(1) }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", ExpiresAt = now.AddDays(-1) }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", RevokedAt = now }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", UsedAt = now }.IsAlive(now));
    }

    [Fact]
    public void A_person_made_by_hand_is_approved_at_once()
    {
        Assert.NotNull(new UserRow { Login = "ivan", PasswordHash = "x" }.ApprovedAt);
    }
}
