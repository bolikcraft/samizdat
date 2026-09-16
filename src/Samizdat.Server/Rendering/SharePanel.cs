using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Endpoints;

namespace Samizdat.Server.Rendering;

/// Блок «Поделиться» рисуется мимо кэша страницы: ссылка живёт своей жизнью и меняется без
/// правки статьи. В кэшированном html вместо блока стоит Placeholder.
public static class SharePanel
{
    public const string Placeholder = "__SHARE_PANEL__";

    public static string Render(PageRenderer pages, SamizdatDbContext db, string slug,
                                IAntiforgery antiforgery, HttpContext context, ClaimsPrincipal user,
                                bool canShare)
    {
        if (!canShare) return "";

        var now = DateTimeOffset.UtcNow;
        var live = db.ShareLinks.Where(link => link.Slug == slug).AsEnumerable()
            .FirstOrDefault(link => link.IsAlive(now));
        var canManage = live is not null
                        && ArticleAccess.CanManageShare(ArticleAccess.RoleOf(user), live.CreatedByUserId,
                                                        SettingsPage.CurrentUser(db, user)?.Id);

        return pages.RenderPart("share-panel.html", new()
        {
            ["slug"] = slug,
            ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
            ["link"] = live is null ? null : new Dictionary<string, object?>
            {
                ["url"] = $"{context.Request.Scheme}://{context.Request.Host}/s/{live.Token}",
                ["note"] = live.Note,
                ["expires_at"] = live.ExpiresAt?.ToString("yyyy-MM-dd HH:mm"),
                ["opened_count"] = live.OpenedCount,
                ["can_manage"] = canManage,
            },
        });
    }
}
