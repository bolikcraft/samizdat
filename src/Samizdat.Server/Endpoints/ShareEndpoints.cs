using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Samizdat.Core;
using Samizdat.Core.Rendering;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Rendering;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class ShareEndpoints
{
    static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapShare(this WebApplication app)
    {
        app.MapGet("/s/{token}", (string token, SamizdatDbContext db, ArticleFiles files,
                                  PageRenderer pages, ArticleRenderer markdown, PageCache cache,
                                  IThemeSource theme, SiteSettings settings, HttpContext context) =>
        {
            // Ставим до любого ответа: после отзыва ссылки страница не должна лежать в браузере или прокси.
            context.Response.Headers.CacheControl = "no-store";

            var link = db.ShareLinks.FirstOrDefault(row => row.Token == token);
            if (link is null) return NotFound(pages, settings);
            if (!link.IsAlive(DateTimeOffset.UtcNow)) return Expired(pages, settings);

            var row = db.Articles.Find(link.Slug);
            if (row is null || !files.MarkdownExists(link.Slug)) return NotFound(pages, settings);

            // Отдельный ключ кэша: у гостя другой html, без дерева и меню. Отпечаток каталога
            // не нужен — на гостевой странице нет списка статей.
            // Разделитель "/" в slug запрещён, поэтому ключ гостя не может совпасть с ключом статьи.
            var html = cache.GetOrBuild($"share/{link.Slug}", row.ContentHash, theme.Version, "",
                                        settings.ViewFingerprint, () =>
            {
                var text = files.ReadMarkdown(link.Slug)!;
                var parsed = FrontMatterParser.Parse(text);

                return pages.Render("article.html", new()
                {
                    ["page_title"] = parsed.FrontMatter.Title ?? link.Slug,
                    ["description"] = parsed.FrontMatter.Description,
                    ["site"] = PageEndpoints.SiteModel(settings),
                    ["noindex"] = true,
                    ["article"] = new Dictionary<string, object?>
                    {
                        ["slug"] = link.Slug,
                        ["title"] = parsed.FrontMatter.Title ?? link.Slug,
                        ["description"] = parsed.FrontMatter.Description,
                        ["date"] = parsed.FrontMatter.Date?.ToString("yyyy-MM-dd"),
                        // NoArticles: любая вики-ссылка станет текстом, чужие slug не утекают.
                        ["html"] = markdown.Render(parsed.Body, link.Slug, NoArticles.Instance, AttachmentBase),
                    },
                    // nav и user пусты: тема не рисует ни боковика, ни меню владельца.
                });
            });

            // Одним запросом к базе, а не чтением и записью: параллельные открытия не теряют счёт.
            var now = DateTimeOffset.UtcNow;
            db.ShareLinks.Where(row => row.Id == link.Id)
                .ExecuteUpdate(set => set
                    .SetProperty(row => row.OpenedCount, row => row.OpenedCount + 1)
                    .SetProperty(row => row.LastOpenedAt, now));

            return Results.Content(html.Replace(AttachmentBase, $"/s/{token}/"), "text/html; charset=utf-8");
        }).AllowAnonymous();

        // Счётчик открытий тут не трогаем: статья с тремя картинками дала бы четыре открытия.
        app.MapGet("/s/{token}/{file}", (string token, string file, SamizdatDbContext db, ArticleFiles files,
                                        HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            var link = db.ShareLinks.FirstOrDefault(row => row.Token == token);
            if (link is null || !link.IsAlive(DateTimeOffset.UtcNow)) return Results.NotFound();

            // Slug берётся из ссылки, а не из запроса: по чужому файлу этот токен не пройдёт.
            var path = files.AttachmentPath(link.Slug, file);
            if (path is null) return Results.NotFound();

            var type = ContentTypes.TryGetContentType(path, out var found) ? found : "application/octet-stream";
            return Results.File(path, type);
        }).AllowAnonymous();

        app.MapPost("/share", async (HttpContext context, SamizdatDbContext db) =>
        {
            var form = await context.Request.ReadFormAsync();
            var slug = form["slug"].ToString();
            var note = form["note"].ToString().Trim();

            if (!int.TryParse(form["days"], out var days) || !AllowedDays.Contains(days))
                return Results.BadRequest();
            if (note.Length > 200) return Results.BadRequest();
            // Пустой slug — испорченная форма, а не «статьи нет»: спека обещает тут 400.
            if (slug.Length == 0) return Results.BadRequest();
            if (db.Articles.Find(slug) is null) return Results.NotFound();

            var link = new ShareLinkRow
            {
                Token = ShareToken.Create(),
                Slug = slug,
                Note = note.Length == 0 ? null : note,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = days == 0 ? null : DateTimeOffset.UtcNow.AddDays(days),
            };
            db.ShareLinks.Add(link);
            db.SaveChanges();

            // Готовый урл показываем в настройках: та страница не кэшируется, там же список и отзыв.
            return Results.Redirect($"/settings#link-{link.Id}");
        }).RequireAuthorization(policy => policy.RequireRole(nameof(UserRole.Owner))).RequireValidToken();
    }

    // День, неделя, месяц, год и «без срока»: другие значения формой не выдаются и не принимаются.
    static readonly int[] AllowedDays = [0, 1, 7, 30, 365];

    // Base вложений в кэшированном html — плейсхолдер: html один на статью, а токен у каждой ссылки свой.
    internal const string AttachmentBase = "__SHARE_BASE__/";

    static IResult NotFound(PageRenderer pages, SiteSettings settings)
        => Results.Content(pages.Render("404.html", new()
        {
            ["page_title"] = "Не найдено",
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
        }), "text/html; charset=utf-8", statusCode: 404);

    static IResult Expired(PageRenderer pages, SiteSettings settings)
        => Results.Content(pages.Render("share-expired.html", new()
        {
            ["page_title"] = "Ссылка не работает",
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
        }), "text/html; charset=utf-8", statusCode: 410);
}
