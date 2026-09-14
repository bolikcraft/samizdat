using System.Security.Claims;
using Samizdat.Server.Data;

namespace Samizdat.Server.Auth;

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

    /// null — вошедшего нет или роль в cookie не разбирается: такой гость не читает ничего.
    public static UserRole? RoleOf(ClaimsPrincipal user)
    {
        var found = user.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(found, out var role) ? role : null;
    }

    public static bool IsOwner(ClaimsPrincipal user) => RoleOf(user) == UserRole.Owner;
}
