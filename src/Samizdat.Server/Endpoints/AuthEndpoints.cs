using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;
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
                => LoginPage(pages, settings, text, antiforgery, context, null,
                             LocalUrl(context.Request.Query["ReturnUrl"].ToString())))
            .AllowAnonymous();

        // Токен нужен и анонимной форме: без него чужой сайт впустит жертву в свою учётку (login CSRF).
        app.MapPost("/login", async (HttpContext context, SamizdatDbContext db, PageRenderer pages,
                                     SiteSettings settings, Translator text, IAntiforgery antiforgery,
                                     PasswordGate gate) =>
        {
            var form = await context.Request.ReadFormAsync();
            var login = form["login"].ToString();
            var password = form["password"].ToString();
            var returnUrl = LocalUrl(form["ReturnUrl"].ToString());

            var user = await db.Users.FirstOrDefaultAsync(row => row.Login == login);

            bool matches;
            using (var lease = await gate.Enter(context.RequestAborted))
            {
                if (!lease.IsAcquired) return PasswordGate.Busy();
                // Неизвестный логин тоже платит за Argon2id: иначе ответ по времени выдаёт, что логина нет.
                matches = PasswordHasher.Verify(password, user?.PasswordHash ?? PasswordHasher.Decoy);
            }
            if (user is null || !matches)
                return LoginPage(pages, settings, text, antiforgery, context,
                                 text["login.err.bad_credentials"], returnUrl);

            // Отдельное сообщение, а не «неверный пароль»: иначе человек решит, что опечатался,
            // и будет бить в форму. То, что такой логин есть, форма регистрации и так говорит
            // вслух словом «занят».
            if (user.ApprovedAt is null)
                return LoginPage(pages, settings, text, antiforgery, context,
                                 text["login.err.not_approved"], returnUrl);

            await SessionCookie.SignIn(context, user);

            return Results.Redirect(returnUrl ?? "/");
        }).AllowAnonymous().RequireValidToken(async context =>
        {
            // Токен на форме сверен с личностью на момент открытия страницы: сосед-вкладка успел
            // войти или выйти, токен разошёлся с текущей учёткой. Не 400, а увести дальше без входа.
            if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");

            // ReturnUrl едет скрытым полем формы, не query: страница входа шлёт POST на "/login" без него.
            var returnUrl = LocalUrl(context.Request.Query["ReturnUrl"].ToString());
            if (returnUrl is null && context.Request.HasFormContentType)
                returnUrl = LocalUrl((await context.Request.ReadFormAsync())["ReturnUrl"].ToString());

            return Results.Redirect(returnUrl is null
                ? "/login"
                : QueryHelpers.AddQueryString("/login", "ReturnUrl", returnUrl));
        }).RequireRateLimiting(AuthLimits.Policy);

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireValidToken();
    }

    /// Адрес для возврата после входа. null — адреса нет или он ведёт за пределы сайта.
    // Первый знак '/' отсекает "~/…": IsLocalUrl его пропускает, а браузер такой адрес не поймёт.
    static string? LocalUrl(string url)
        => url is ['/', ..] && Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult.IsLocalUrl(url) ? url : null;

    static IResult LoginPage(PageRenderer pages, SiteSettings settings, Translator text, IAntiforgery antiforgery,
                             HttpContext context, string? error, string? returnUrl)
        => Results.Content(
            pages.Render("login.html", new()
            {
                ["page_title"] = text["login.title"],
                ["error"] = error,
                ["registration_open"] = settings.OpenRegistration,
                ["site"] = PageEndpoints.SiteModel(settings),
                ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
                ["return_url"] = returnUrl,
            }),
            "text/html; charset=utf-8");
}
