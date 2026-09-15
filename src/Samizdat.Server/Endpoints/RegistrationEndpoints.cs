using Microsoft.AspNetCore.Antiforgery;
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
    // Предел очереди — единственный барьер, пока нет ни почты, ни капчи: бот набьёт полсотни
    // заявок и упрётся, а не наплодит их тысячами.
    const int MaxPending = 50;

    // Номер блокировки, под которой считают очередь. Число произвольное, важно лишь то, что
    // его берут все заявки разом и никто больше.
    const long QueueLock = 761_923_401;

    public static void MapRegistration(this WebApplication app)
    {
        app.MapGet("/i/{token}", (string token, HttpContext context, SamizdatDbContext db,
                                  PageRenderer pages, SiteSettings settings, IAntiforgery antiforgery) =>
        {
            // Ставим до любого ответа: после того как ссылку погасили, форма не должна лежать
            // в браузере или прокси.
            context.Response.Headers.CacheControl = "no-store";

            // Вошедшему учётка уже не нужна, а ссылку он бы сжёг и потерял свою сессию.
            if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");

            var invite = db.Invites.AsNoTracking().FirstOrDefault(row => row.Token == token);
            if (invite is null) return GuestPages.NotFound(pages, settings);
            if (!invite.IsAlive(DateTimeOffset.UtcNow)) return GuestPages.Gone(pages, settings);

            return Form(pages, settings, antiforgery, context, $"/i/{token}", invite.Note,
                        login: "", error: null);
        }).AllowAnonymous();

        app.MapPost("/i/{token}", async (string token, HttpContext context, SamizdatDbContext db,
                                         PageRenderer pages, SiteSettings settings,
                                         IAntiforgery antiforgery) =>
        {
            if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");

            // Развилка 404/410 и источник заметки для формы. Настоящий замок ниже — условие
            // внутри UPDATE; без него эта проверка пропускает обе вкладки разом.
            var invite = await db.Invites.AsNoTracking().FirstOrDefaultAsync(row => row.Token == token);
            if (invite is null) return GuestPages.NotFound(pages, settings);
            if (!invite.IsAlive(DateTimeOffset.UtcNow)) return GuestPages.Gone(pages, settings);

            var form = await RegistrationForm.Read(context);
            if (form.Fault() is { } fault)
                return Form(pages, settings, antiforgery, context, $"/i/{token}", invite.Note,
                            form.Login, fault);

            // Хэш считается до транзакции: Argon2id занимает десятые доли секунды, и соседняя
            // вкладка ждала бы их на блокировке строки.
            var now = DateTimeOffset.UtcNow;
            var person = form.ToReader(now, approved: true);

            await using var transaction = await db.Database.BeginTransactionAsync();

            // Условие внутри UPDATE, а не проверка перед ним: две вкладки на одной ссылке ждут
            // друг друга на блокировке строки, и второй достаётся ноль изменённых строк.
            var burned = await db.Invites
                .Where(row => row.Token == token && row.UsedAt == null && row.RevokedAt == null
                              && (row.ExpiresAt == null || row.ExpiresAt > now))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.UsedAt, now)
                    .SetProperty(row => row.UsedByLogin, form.Login));
            if (burned == 0)
            {
                await transaction.RollbackAsync();
                return GuestPages.Gone(pages, settings);
            }

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
                // Забываем строку: в базе её нет, а трекер держал бы её к следующему сохранению.
                db.Entry(person).State = EntityState.Detached;
                return Form(pages, settings, antiforgery, context, $"/i/{token}", invite.Note,
                            form.Login, "Такой логин уже занят.");
            }

            await transaction.CommitAsync();
            await SessionCookie.SignIn(context, person);
            return Results.Redirect("/");
        }).AllowAnonymous().RequireValidToken();

        app.MapGet("/register", (HttpContext context, PageRenderer pages, SiteSettings settings,
                                 IAntiforgery antiforgery) =>
        {
            if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");

            return Form(pages, settings, antiforgery, context, "/register", note: null,
                        login: "", error: null);
        }).AllowAnonymous().RefuseWhenClosed();

        app.MapPost("/register", async (HttpContext context, SamizdatDbContext db, PageRenderer pages,
                                        SiteSettings settings, IAntiforgery antiforgery) =>
        {
            if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/");

            var form = await RegistrationForm.Read(context);
            if (form.Fault() is { } fault)
                return Form(pages, settings, antiforgery, context, "/register", note: null,
                            form.Login, fault);

            // Хэш считается до транзакции: Argon2id занимает десятые доли секунды, и соседняя
            // заявка ждала бы их под блокировкой очереди.
            var person = form.ToReader(DateTimeOffset.UtcNow, approved: false);

            // Считают и вставляют под общей блокировкой: без неё параллельные заявки читают один
            // и тот же счётчик и проходят предел все сразу.
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({QueueLock})");

            if (await db.Users.CountAsync(row => row.ApprovedAt == null) >= MaxPending)
            {
                await transaction.RollbackAsync();
                return Form(pages, settings, antiforgery, context, "/register", note: null, form.Login,
                            "Регистрация временно закрыта: слишком много заявок ждут ответа.");
            }

            db.Users.Add(person);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException error)
                when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                await transaction.RollbackAsync();
                // Забываем строку: в базе её нет, а трекер держал бы её к следующему сохранению.
                db.Entry(person).State = EntityState.Detached;
                return Form(pages, settings, antiforgery, context, "/register", note: null,
                            form.Login, "Такой логин уже занят.");
            }

            await transaction.CommitAsync();
            return Results.Content(pages.Render("register-sent.html", new()
            {
                ["page_title"] = "Заявка отправлена",
                ["site"] = PageEndpoints.SiteModel(settings),
                ["noindex"] = true,
            }), "text/html; charset=utf-8");
        }).AllowAnonymous().RefuseWhenClosed().RequireValidToken();
    }

    /// Закрытая регистрация отвечает «нет такой страницы» раньше, чем проверка токена.
    // Обязан стоять до RequireValidToken: фильтры отрабатывают прежде тела обработчика, и форма
    // без токена получала бы 400 — то есть ответ выдавал бы, что маршрут всё-таки есть.
    static RouteHandlerBuilder RefuseWhenClosed(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (invocation, next) =>
        {
            var services = invocation.HttpContext.RequestServices;
            var settings = services.GetRequiredService<SiteSettings>();

            return settings.OpenRegistration
                ? await next(invocation)
                : GuestPages.NotFound(services.GetRequiredService<PageRenderer>(), settings);
        });

    static IResult Form(PageRenderer pages, SiteSettings settings, IAntiforgery antiforgery,
                        HttpContext context, string action, string? note, string login, string? error)
        => Results.Content(pages.Render("register.html", new()
        {
            ["page_title"] = "Регистрация",
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
            ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
            ["action"] = action,
            ["note"] = note,
            ["login"] = login,
            ["error"] = error,
        }), "text/html; charset=utf-8");
}
