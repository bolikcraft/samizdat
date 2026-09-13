using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/login", (PageRenderer pages, SiteSettings settings) => LoginPage(pages, settings, null))
            .AllowAnonymous();

        app.MapPost("/login", async (HttpContext context, SamizdatDbContext db, PageRenderer pages,
                                     SiteSettings settings) =>
        {
            var form = await context.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var password = form["password"].ToString();

            var user = await db.Users.FirstOrDefaultAsync(row => row.Login == login);
            if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
                return LoginPage(pages, settings, "Неверный логин или пароль");

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, user.Login), new Claim(ClaimTypes.Role, user.Role.ToString())],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                                      new ClaimsPrincipal(identity));

            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireValidToken();
    }

    static IResult LoginPage(PageRenderer pages, SiteSettings settings, string? error)
        => Results.Content(
            pages.Render("login.html", new()
            {
                ["page_title"] = "Вход",
                ["error"] = error,
                ["site"] = PageEndpoints.SiteModel(settings),
            }),
            "text/html; charset=utf-8");
}
