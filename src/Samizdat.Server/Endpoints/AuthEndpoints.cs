using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Samizdat.Core.Localization;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/login", (HttpContext context, PageRenderer pages, SiteSettings settings, Translator text,
                              IAntiforgery antiforgery)
                => LoginPage(pages, settings, text, antiforgery, context, null))
            .AllowAnonymous();

        // Токен нужен и анонимной форме: без него чужой сайт впустит жертву в свою учётку (login CSRF).
        app.MapPost("/login", async (HttpContext context, SamizdatDbContext db, PageRenderer pages,
                                     SiteSettings settings, Translator text, IAntiforgery antiforgery) =>
        {
            var form = await context.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var password = form["password"].ToString();

            var user = await db.Users.FirstOrDefaultAsync(row => row.Login == login);
            if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
                return LoginPage(pages, settings, text, antiforgery, context, text["login.err.bad_credentials"]);

            // Отдельное сообщение, а не «неверный пароль»: иначе человек решит, что опечатался,
            // и будет бить в форму. То, что такой логин есть, форма регистрации и так говорит
            // вслух словом «занят».
            if (user.ApprovedAt is null)
                return LoginPage(pages, settings, text, antiforgery, context, text["login.err.not_approved"]);

            await SessionCookie.SignIn(context, user);

            return Results.Redirect("/");
        }).AllowAnonymous().RequireValidToken();

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireValidToken();
    }

    static IResult LoginPage(PageRenderer pages, SiteSettings settings, Translator text, IAntiforgery antiforgery,
                             HttpContext context, string? error)
        => Results.Content(
            pages.Render("login.html", new()
            {
                ["page_title"] = text["login.title"],
                ["error"] = error,
                ["registration_open"] = settings.OpenRegistration,
                ["site"] = PageEndpoints.SiteModel(settings),
                ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
            }),
            "text/html; charset=utf-8");
}
