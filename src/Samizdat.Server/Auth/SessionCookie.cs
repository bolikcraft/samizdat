using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Samizdat.Server.Data;

namespace Samizdat.Server.Auth;

/// Cookie входа и её проверка. Cookie живёт месяц, поэтому в ней едет метка сессии из базы:
/// удалили человека или сменили ему пароль — метка не сходится, и сессия гаснет сразу.
public static class SessionCookie
{
    public const string StampClaim = "samizdat:stamp";

    public static Task SignIn(HttpContext context, UserRow user)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Login),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(StampClaim, user.SessionStamp),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);

        return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                                   new ClaimsPrincipal(identity));
    }

    /// Запрос к базе на каждый запрос с cookie: за него платим ради того, чтобы удалённый человек
    /// терял доступ сразу, а не доживал месяц до конца срока cookie.
    public static async Task Validate(CookieValidatePrincipalContext context)
    {
        var login = context.Principal?.Identity?.Name;
        var stamp = context.Principal?.FindFirstValue(StampClaim);
        if (login is not null && stamp is not null)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<SamizdatDbContext>();
            // ApprovedAt едет тем же запросом, что и метка: отозвать одобрение можно, и сессия
            // должна погаснуть так же быстро, как от смены пароля.
            var current = await db.Users.AsNoTracking()
                .Where(row => row.Login == login)
                .Select(row => new { row.SessionStamp, row.ApprovedAt })
                .FirstOrDefaultAsync();
            if (current is not null && current.SessionStamp == stamp && current.ApprovedAt is not null) return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
