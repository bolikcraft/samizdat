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
    [InlineData(UserRole.Owner, null, 5, true)]
    [InlineData(UserRole.Owner, 7, 5, true)]
    [InlineData(UserRole.Reader, 5, 5, true)]
    [InlineData(UserRole.Reader, 7, 5, false)]
    // Ссылка без автора выдана до того, как автора стали записывать: она владельческая.
    [InlineData(UserRole.Reader, null, 5, false)]
    [InlineData(null, 5, 5, false)]
    public void Only_the_author_and_the_owner_manage_a_link(UserRole? role, int? authorId, int? userId, bool allowed)
        => Assert.Equal(allowed, ArticleAccess.CanManageShare(role, authorId, userId));

    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Reader, false)]
    [InlineData(null, false)]
    public void Only_the_owner_switches_visibility(UserRole? role, bool allowed)
        => Assert.Equal(allowed, ArticleAccess.CanSwitchVisibility(role));

    [Theory]
    // Владельцу переключатель не указ: своё он качает всегда.
    [InlineData(ArticleVisibility.Private, UserRole.Owner, false, true)]
    [InlineData(ArticleVisibility.Shared, UserRole.Owner, false, true)]
    [InlineData(ArticleVisibility.Shared, UserRole.Reader, false, false)]
    [InlineData(ArticleVisibility.Shared, UserRole.Reader, true, true)]
    // Закрытую статью читатель не качает даже при включенном переключателе: он её и не видит.
    [InlineData(ArticleVisibility.Private, UserRole.Reader, true, false)]
    [InlineData(ArticleVisibility.Shared, null, true, false)]
    public void Downloading_follows_the_switch_for_everyone_but_the_owner(
        ArticleVisibility visibility, UserRole? role, bool readers, bool allowed)
        => Assert.Equal(allowed, ArticleAccess.CanDownload(visibility, role, new DownloadPolicy(readers, false)));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_guest_downloads_only_when_the_guest_switch_is_on(bool guests, bool allowed)
        => Assert.Equal(allowed, ArticleAccess.CanDownloadByShare(new DownloadPolicy(false, guests)));
}
