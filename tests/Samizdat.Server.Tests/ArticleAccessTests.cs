using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

public class ArticleAccessTests
{
    [Theory]
    [InlineData(ArticleVisibility.Private, UserRole.Owner, true)]
    [InlineData(ArticleVisibility.Shared, UserRole.Owner, true)]
    [InlineData(ArticleVisibility.Private, UserRole.Reader, false)]
    [InlineData(ArticleVisibility.Shared, UserRole.Reader, true)]
    [InlineData(ArticleVisibility.Private, null, false)]
    [InlineData(ArticleVisibility.Shared, null, false)]
    public void Reading_follows_visibility_and_role(ArticleVisibility visibility, UserRole? role, bool allowed)
        => Assert.Equal(allowed, ArticleAccess.CanRead(visibility, role));

    [Theory]
    [InlineData(ArticleVisibility.Private, UserRole.Owner, true)]
    [InlineData(ArticleVisibility.Shared, UserRole.Reader, true)]
    [InlineData(ArticleVisibility.Private, UserRole.Reader, false)]
    [InlineData(ArticleVisibility.Shared, null, false)]
    public void Sharing_needs_the_same_access_as_reading(ArticleVisibility visibility, UserRole? role, bool allowed)
        => Assert.Equal(allowed, ArticleAccess.CanShare(visibility, role));

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Reader, false)]
    [InlineData(null, false)]
    public void Only_the_owner_switches_visibility(UserRole? role, bool allowed)
        => Assert.Equal(allowed, ArticleAccess.CanSwitchVisibility(role));
}
