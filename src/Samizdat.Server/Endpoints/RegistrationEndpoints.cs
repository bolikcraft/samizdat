using Microsoft.EntityFrameworkCore;
using Npgsql;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

/// Два пути, которыми человек заводит себе учётку сам: одноразовое приглашение и открытая
/// запись. Оба анонимные — их зовут до всякого входа.
public static class RegistrationEndpoints
{
    public static void MapRegistration(this WebApplication app)
    {
        app.MapGet("/i/{token}", (string token, HttpContext context, SamizdatDbContext db,
                                  PageRenderer pages, SiteSettings settings) =>
        {
            // Ставим до любого ответа: после того как ссылку погасили, форма не должна лежать
            // в браузере или прокси.
            context.Response.Headers.CacheControl = "no-store";

            var invite = db.Invites.AsNoTracking().FirstOrDefault(row => row.Token == token);
            if (invite is null) return NotFound(pages, settings);
            if (!invite.IsAlive(DateTimeOffset.UtcNow)) return Dead(pages, settings);

            return Form(pages, settings, $"/i/{token}", invite.Note, error: null);
        }).AllowAnonymous();

        app.MapPost("/i/{token}", async (string token, HttpContext context, SamizdatDbContext db,
                                         PageRenderer pages, SiteSettings settings) =>
        {
            var invite = db.Invites.AsNoTracking().FirstOrDefault(row => row.Token == token);
            if (invite is null) return NotFound(pages, settings);
            if (!invite.IsAlive(DateTimeOffset.UtcNow)) return Dead(pages, settings);

            var form = await RegistrationForm.Read(context);
            if (form.Fault() is { } fault) return Form(pages, settings, $"/i/{token}", invite.Note, fault);

            var now = DateTimeOffset.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync();

            // Условие внутри UPDATE, а не проверка перед ним: две вкладки на одной ссылке ждут
            // друг друга на блокировке строки, и второй достаётся ноль изменённых строк.
            var burned = await db.Invites
                .Where(row => row.Token == token && row.UsedAt == null && row.RevokedAt == null
                              && (row.ExpiresAt == null || row.ExpiresAt > now))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.UsedAt, now)
                    .SetProperty(row => row.UsedByLogin, form.Login));
            if (burned == 0) return Dead(pages, settings);

            var person = form.ToReader(now, approved: true);
            db.Users.Add(person);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException error)
                when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Занятый логин слышим от базы, как везде. Откат возвращает и гашение: сгорать
                // из-за чужого логина ссылка не должна.
                await transaction.RollbackAsync();
                return Form(pages, settings, $"/i/{token}", invite.Note, "Такой логин уже занят.");
            }

            await transaction.CommitAsync();
            await SessionCookie.SignIn(context, person);
            return Results.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();
    }

    static IResult Form(PageRenderer pages, SiteSettings settings, string action, string? note, string? error)
        => Results.Content(pages.Render("register.html", new()
        {
            ["page_title"] = "Регистрация",
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
            ["action"] = action,
            ["note"] = note,
            ["error"] = error,
        }), "text/html; charset=utf-8");

    static IResult NotFound(PageRenderer pages, SiteSettings settings)
        => Results.Content(pages.Render("404.html", new()
        {
            ["page_title"] = "Не найдено",
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
        }), "text/html; charset=utf-8", statusCode: 404);

    static IResult Dead(PageRenderer pages, SiteSettings settings)
        => Results.Content(pages.Render("share-expired.html", new()
        {
            ["page_title"] = "Ссылка не работает",
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
        }), "text/html; charset=utf-8", statusCode: 410);
}
