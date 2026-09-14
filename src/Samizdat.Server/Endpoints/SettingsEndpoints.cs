using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Rendering;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class SettingsEndpoints
{
    const long MaxBackgroundBytes = 8 * 1024 * 1024;

    // Многочастная форма добавляет к файлу границы и заголовки полей — запас с лихвой их перекрывает.
    // Предел стоит на всём теле: такое тело мы отказываемся буферизовать, даже не начиная читать.
    const long MaxBackgroundRequestBytes = MaxBackgroundBytes + 64 * 1024;

    public static void MapSettings(this WebApplication app)
    {
        var group = app.MapGroup("/settings")
            .RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.Owner)));

        group.MapGet("/", (PageRenderer pages, SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                           ThemeFactory themes, IAntiforgery antiforgery, HttpContext context,
                           string? ok, string? err) =>
        {
            var owner = CurrentUser(db, user);
            var tokens = db.ApiTokens.Where(token => token.UserId == owner.Id)
                .OrderByDescending(token => token.CreatedAt).ToList();

            var now = DateTimeOffset.UtcNow;
            var titles = db.Articles.ToDictionary(article => article.Slug, article => article.Title);
            var closed = db.Articles.Where(article => article.Visibility == ArticleVisibility.Private)
                .Select(article => article.Slug).ToHashSet();
            // Живые сверху: мёртвые строки остаются как след, но не мешают найти рабочую ссылку.
            var links = db.ShareLinks.ToList()
                .OrderByDescending(link => link.IsAlive(now)).ThenByDescending(link => link.CreatedAt)
                .Select(link => new Dictionary<string, object?>
                {
                    ["id"] = link.Id,
                    ["slug"] = link.Slug,
                    ["title"] = titles.GetValueOrDefault(link.Slug, link.Slug),
                    ["note"] = link.Note,
                    ["url"] = $"{context.Request.Scheme}://{context.Request.Host}/s/{link.Token}",
                    ["alive"] = link.IsAlive(now),
                    // Статью закрыли — ссылка жива, но гостю не открывается.
                    ["sleeping"] = link.IsAlive(now) && closed.Contains(link.Slug),
                    ["expires_at"] = link.ExpiresAt?.ToString("yyyy-MM-dd HH:mm"),
                    ["opened_count"] = link.OpenedCount,
                    ["last_opened_at"] = link.LastOpenedAt?.ToString("yyyy-MM-dd HH:mm"),
                }).ToList();

            return Results.Content(pages.Render("settings.html", new()
            {
                ["page_title"] = "Настройки",
                ["site"] = PageEndpoints.SiteModel(settings),
                // Вместо дерева статей в боковике — список разделов настроек.
                ["side_nav"] = "settings-nav",
                ["user"] = PageEndpoints.UserModel(user),
                ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
                ["message"] = Message(ok, err),
                ["message_kind"] = err is not null ? "err" : ok is not null ? "ok" : null,
                ["color_scheme"] = settings.ColorScheme,
                ["themes"] = themes.AvailableThemes().Select(name => new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["selected"] = name == settings.ThemeName,
                }).ToList(),
                ["tokens"] = tokens.Select(token => new Dictionary<string, object?>
                {
                    ["id"] = token.Id,
                    ["note"] = token.Note,
                    ["created_at"] = token.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                    ["last_used_at"] = token.LastUsedAt?.ToString("yyyy-MM-dd HH:mm"),
                }).ToList(),
                ["links"] = links,
            }), "text/html; charset=utf-8");
        });

        // Якорь в конце адреса — раздел настроек: страница показывает тот, чей якорь стоит в адресе.
        // Без него после отправки формы открывался бы первый раздел, а не тот, где нажали кнопку.
        group.MapPost("/password", async (HttpContext context, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            var form = await context.Request.ReadFormAsync();
            var current = form["current"].ToString();
            var next = form["new"].ToString();
            var repeat = form["new2"].ToString();

            var owner = CurrentUser(db, user);

            if (!PasswordHasher.Verify(current, owner.PasswordHash))
                return Results.Redirect("/settings?err=wrong_password#security");
            if (next.Length < 8)
                return Results.Redirect("/settings?err=short_password#security");
            if (next != repeat)
                return Results.Redirect("/settings?err=password_mismatch#security");

            owner.PasswordHash = PasswordHasher.Hash(next);
            db.SaveChanges();
            return Results.Redirect("/settings?ok=password#security");
        }).RequireValidToken();

        group.MapPost("/appearance", async (HttpContext context, SiteSettings settings, ThemeFactory themes) =>
        {
            var form = await context.Request.ReadFormAsync();
            var theme = form["theme"].ToString();
            var colorScheme = form["color_scheme"].ToString();

            if (themes.AvailableThemes().Contains(theme)) settings.Set("theme.name", theme);
            if (colorScheme is "light" or "dark" or "system") settings.Set("theme.color_scheme", colorScheme);

            return Results.Redirect("/settings?ok=appearance#appearance");
        }).RequireValidToken();

        group.MapPost("/background", [RequestSizeLimit(MaxBackgroundRequestBytes)]
            async (HttpContext context, SiteSettings settings, BackgroundFile background) =>
        {
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync();
            }
            catch (BadHttpRequestException)
            {
                // Тело больше лимита: Kestrel обрывает чтение сам, не дав ReadFormAsync его дочитать.
                return Results.Redirect("/settings?err=background_too_big#appearance");
            }
            catch (InvalidDataException)
            {
                // Форма нечитаема: оборванная граница, слишком много полей, слишком длинный ключ.
                return Results.Redirect("/settings?err=background_form#appearance");
            }

            var upload = form.Files["file"];
            if (upload is null || upload.Length == 0)
                return Results.Redirect("/settings?err=background_missing#appearance");
            if (upload.Length > MaxBackgroundBytes)
                return Results.Redirect("/settings?err=background_too_big#appearance");

            await using var stream = upload.OpenReadStream();
            var head = new byte[BackgroundFile.HeadLength];
            var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
            if (BackgroundFile.ExtensionOf(head.AsSpan(0, read)) is not { } extension)
                return Results.Redirect("/settings?err=background_type#appearance");

            stream.Position = 0;
            settings.Set("theme.background", background.Save(stream, extension));
            return Results.Redirect("/settings?ok=background#appearance");
        }).RefuseAnOversizedBody().RequireValidToken();

        group.MapPost("/background/remove", (SiteSettings settings, BackgroundFile background) =>
        {
            background.Remove();
            settings.Set("theme.background", "");
            return Results.Redirect("/settings?ok=background_removed#appearance");
        }).RequireValidToken();

        group.MapPost("/tokens/{id:int}/revoke", (int id, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            var owner = CurrentUser(db, user);
            var token = db.ApiTokens.FirstOrDefault(row => row.Id == id);
            if (token is null || token.UserId != owner.Id) return Results.NotFound();

            db.ApiTokens.Remove(token);
            db.SaveChanges();
            return Results.Redirect("/settings?ok=token_revoked#tokens");
        }).RequireValidToken();

        // Отзыв мягкий: строка остаётся, чтобы гость получил 410 «ссылка не работает», а не 404.
        group.MapPost("/links/{id:int}/revoke", (int id, SamizdatDbContext db) =>
        {
            var link = db.ShareLinks.FirstOrDefault(row => row.Id == id && row.RevokedAt == null);
            if (link is null) return Results.NotFound();

            link.RevokedAt = DateTimeOffset.UtcNow;
            db.SaveChanges();
            return Results.Redirect("/settings?ok=link_revoked#links");
        }).RequireValidToken();
    }

    /// Отсеивает слишком большое тело по Content-Length, ничего не читая.
    // Обязан стоять до RequireValidToken: тот ради токена читает многочастную форму сам, Kestrel
    // обрывает чтение прямо в нём, и владелец получает голый 400 вместо сообщения про 8 МБ.
    // [RequestSizeLimit] на маршруте оставлен: он ловит тело без Content-Length (chunked).
    static RouteHandlerBuilder RefuseAnOversizedBody(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (invocation, next) =>
            invocation.HttpContext.Request.ContentLength > MaxBackgroundRequestBytes
                ? Results.Redirect("/settings?err=background_too_big#appearance")
                : await next(invocation));

    static UserRow CurrentUser(SamizdatDbContext db, ClaimsPrincipal user)
        => db.Users.First(row => row.Login == user.Identity!.Name);

    static string? Message(string? ok, string? err) => err switch
    {
        "wrong_password" => "Неверный текущий пароль.",
        "short_password" => "Новый пароль должен быть не короче 8 символов.",
        "password_mismatch" => "Новый пароль и повтор не совпадают.",
        "background_missing" => "Файл не выбран.",
        "background_type" => "Это не картинка. Подойдёт jpeg, png или webp.",
        "background_too_big" => "Картинка больше 8 МБ.",
        not null => "Не удалось выполнить действие.",
        null => ok switch
        {
            "password" => "Пароль изменён.",
            "appearance" => "Настройки внешнего вида сохранены.",
            "token_revoked" => "Токен отозван.",
            "link_revoked" => "Ссылка отозвана.",
            "background" => "Фон загружен.",
            "background_removed" => "Фон убран.",
            _ => null,
        },
    };
}
