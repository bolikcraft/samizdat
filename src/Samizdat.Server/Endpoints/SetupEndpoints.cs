using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Samizdat.Core.Localization;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

/// Форма первого входа: человек с коротким паролем задаёт новый пароль и может сменить логин.
/// Поля и правила — как у регистрации.
public static class SetupEndpoints
{
    public static void MapSetup(this WebApplication app)
    {
        app.MapGet("/setup", (HttpContext context, ClaimsPrincipal user, PageRenderer pages, SiteSettings settings,
                              Translator text, IAntiforgery antiforgery)
                => SessionCookie.NeedsSetup(user)
                    ? Form(pages, settings, text, antiforgery, context, user.Identity!.Name!, error: null)
                    : Results.Redirect("/"))
            .RequireAuthorization();

        app.MapPost("/setup", async (HttpContext context, ClaimsPrincipal user, SamizdatDbContext db,
                                     PageRenderer pages, SiteSettings settings, Translator text,
                                     IAntiforgery antiforgery, PasswordGate gate) =>
        {
            // Форма меняет пароль, не спрашивая текущий, поэтому без метки смены она не работает.
            if (!SessionCookie.NeedsSetup(user)) return Results.Redirect("/");
            if (SettingsPage.CurrentUser(db, user) is not { } person) return SettingsPage.LoggedOut();

            var form = await RegistrationForm.Read(context);
            if (form.Fault(text) is { } fault)
                return Form(pages, settings, text, antiforgery, context, form.Login, fault);

            // Свой логин занятым не считается: его можно оставить или сменить в нём регистр.
            if (await db.Users.AnyAsync(row => row.Login == form.Login && row.Id != person.Id))
                return Form(pages, settings, text, antiforgery, context, form.Login,
                            text["register.err.login_taken"]);

            using (var lease = await gate.Enter(context.RequestAborted))
            {
                if (!lease.IsAcquired) return PasswordGate.Busy();
                person.PasswordHash = PasswordHasher.Hash(form.Password);
            }
            person.Login = form.Login;
            // Новая метка гасит прочие cookie этого человека, в том числе с меткой смены.
            person.SessionStamp = UserRow.NewSessionStamp();

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException error)
                when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return Form(pages, settings, text, antiforgery, context, form.Login,
                            text["register.err.login_taken"]);
            }

            await SessionCookie.SignIn(context, person);
            return Results.Redirect("/");
        }).RequireAuthorization().RequireValidToken();
    }

    static IResult Form(PageRenderer pages, SiteSettings settings, Translator text, IAntiforgery antiforgery,
                        HttpContext context, string login, string? error)
        => Results.Content(pages.Render("setup.html", new()
        {
            ["page_title"] = text["setup.title"],
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
            ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
            ["login"] = login,
            ["error"] = error,
        }), "text/html; charset=utf-8");
}
