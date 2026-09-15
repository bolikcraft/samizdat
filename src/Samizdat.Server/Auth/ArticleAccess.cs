using System.Security.Claims;
using Samizdat.Server.Data;

namespace Samizdat.Server.Auth;

/// Кому владелец разрешил скачивать статьи. Самого владельца тут нет: он качает всегда.
public readonly record struct DownloadPolicy(bool Readers, bool Guests);

/// Единственное место, где решается доступ к статье. Без базы и без HttpContext: правило
/// проверяется таблицей в тестах, а маршруты только зовут эти функции.
public static class ArticleAccess
{
    public static bool CanRead(ArticleVisibility visibility, UserRole? role) => role switch
    {
        UserRole.Owner => true,
        UserRole.Reader => visibility == ArticleVisibility.Shared,
        _ => false,
    };

    public static bool CanShare(ArticleVisibility visibility, UserRole? role) => CanRead(visibility, role);

    public static bool CanSwitchVisibility(UserRole? role) => role == UserRole.Owner;

    public static bool CanDownload(ArticleVisibility visibility, UserRole? role, DownloadPolicy policy)
        => CanRead(visibility, role) && (role == UserRole.Owner || policy.Readers);

    /// Гость проверяется отдельно: роли у него нет, а срок ссылки к видимости статьи отношения
    /// не имеет — его сторожит сама ссылка.
    public static bool CanDownloadByShare(DownloadPolicy policy) => policy.Guests;

    /// null — вошедшего нет или роль в cookie не разбирается: такой гость не читает ничего.
    public static UserRole? RoleOf(ClaimsPrincipal user)
    {
        var found = user.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(found, out var role) ? role : null;
    }

    public static bool IsOwner(ClaimsPrincipal user) => RoleOf(user) == UserRole.Owner;
}
