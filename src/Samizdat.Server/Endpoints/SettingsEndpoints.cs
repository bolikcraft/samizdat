using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Rendering;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class SettingsEndpoints
{
    const long MaxBackgroundBytes = 8 * 1024 * 1024;

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
                    ["expires_at"] = link.ExpiresAt?.ToString("yyyy-MM-dd HH:mm"),
                    ["opened_count"] = link.OpenedCount,
                    ["last_opened_at"] = link.LastOpenedAt?.ToString("yyyy-MM-dd HH:mm"),
                }).ToList();

            return Results.Content(pages.Render("settings.html", new()
            {
                ["page_title"] = "Настройки",
                ["site"] = PageEndpoints.SiteModel(settings),
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

        group.MapPost("/password", async (HttpContext context, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            var form = await context.Request.ReadFormAsync();
            var current = form["current"].ToString();
            var next = form["new"].ToString();
            var repeat = form["new2"].ToString();

            var owner = CurrentUser(db, user);

            if (!PasswordHasher.Verify(current, owner.PasswordHash))
                return Results.Redirect("/settings?err=wrong_password");
            if (next.Length < 8)
                return Results.Redirect("/settings?err=short_password");
            if (next != repeat)
                return Results.Redirect("/settings?err=password_mismatch");

            owner.PasswordHash = PasswordHasher.Hash(next);
            db.SaveChanges();
            return Results.Redirect("/settings?ok=password");
        }).RequireValidToken();

        group.MapPost("/appearance", async (HttpContext context, SiteSettings settings, ThemeFactory themes) =>
        {
            var form = await context.Request.ReadFormAsync();
            var theme = form["theme"].ToString();
            var colorScheme = form["color_scheme"].ToString();

            if (themes.AvailableThemes().Contains(theme)) settings.Set("theme.name", theme);
            if (colorScheme is "light" or "dark" or "system") settings.Set("theme.color_scheme", colorScheme);

            return Results.Redirect("/settings?ok=appearance");
        }).RequireValidToken();

        group.MapPost("/background", async (HttpContext context, SiteSettings settings, BackgroundFile background) =>
        {
            var form = await context.Request.ReadFormAsync();
            var upload = form.Files["file"];
            if (upload is null || upload.Length == 0) return Results.Redirect("/settings?err=background_missing");
            if (upload.Length > MaxBackgroundBytes) return Results.Redirect("/settings?err=background_too_big");

            await using var stream = upload.OpenReadStream();
            var head = new byte[BackgroundFile.HeadLength];
            var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false);
            // Тип по первым байтам: расширение и Content-Type ставит браузер, им верить нельзя.
            if (BackgroundFile.ExtensionOf(head.AsSpan(0, read)) is not { } extension)
                return Results.Redirect("/settings?err=background_type");

            stream.Position = 0;
            settings.Set("theme.background", background.Save(stream, extension));
            return Results.Redirect("/settings?ok=background");
        }).RequireValidToken();

        group.MapPost("/background/remove", (SiteSettings settings, BackgroundFile background) =>
        {
            background.Remove();
            settings.Set("theme.background", "");
            return Results.Redirect("/settings?ok=background_removed");
        }).RequireValidToken();

        group.MapPost("/tokens/{id:int}/revoke", (int id, SamizdatDbContext db, ClaimsPrincipal user) =>
        {
            var owner = CurrentUser(db, user);
            var token = db.ApiTokens.FirstOrDefault(row => row.Id == id);
            if (token is null || token.UserId != owner.Id) return Results.NotFound();

            db.ApiTokens.Remove(token);
            db.SaveChanges();
            return Results.Redirect("/settings?ok=token_revoked");
        }).RequireValidToken();

        // Отзыв мягкий: строка остаётся, чтобы гость получил 410 «ссылка не работает», а не 404.
        group.MapPost("/links/{id:int}/revoke", (int id, SamizdatDbContext db) =>
        {
            var link = db.ShareLinks.FirstOrDefault(row => row.Id == id && row.RevokedAt == null);
            if (link is null) return Results.NotFound();

            link.RevokedAt = DateTimeOffset.UtcNow;
            db.SaveChanges();
            return Results.Redirect("/settings?ok=link_revoked");
        }).RequireValidToken();
    }

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
