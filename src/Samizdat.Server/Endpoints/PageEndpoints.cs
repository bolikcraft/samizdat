using System.Security.Claims;
using Microsoft.AspNetCore.StaticFiles;
using Samizdat.Core;
using Samizdat.Core.Navigation;
using Samizdat.Core.Rendering;
using Samizdat.Core.Themes;
using Samizdat.Server.Data;
using Samizdat.Server.Rendering;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class PageEndpoints
{
    static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static RouteGroupBuilder MapPages(this WebApplication app)
    {
        var group = app.MapGroup("");

        group.MapGet("/", (PageRenderer pages, SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user) =>
        {
            var rows = db.Articles.OrderByDescending(article => article.Date).ToList();
            var list = rows.Select(article => new Dictionary<string, object?>
            {
                ["slug"] = article.Slug,
                ["title"] = article.Title,
                ["description"] = article.Description,
                ["date"] = article.Date.HasValue ? article.Date.Value.ToString("yyyy-MM-dd") : null,
            }).ToList();

            return Results.Content(pages.Render("index.html", new()
            {
                ["page_title"] = "Samizdat",
                ["site"] = SiteModel(settings),
                ["articles"] = list,
                ["nav"] = Navigation(db, currentSlug: null),
                ["user"] = UserModel(user),
            }), "text/html; charset=utf-8");
        });

        group.MapGet("/{slug}", (string slug, PageRenderer pages, ArticleFiles files,
                                 ArticleRenderer markdown, IArticleLookup articles,
                                 PageCache cache, IThemeSource theme, SamizdatDbContext db,
                                 SiteSettings settings, ClaimsPrincipal user, ILogger<Program> logger) =>
        {
            var row = db.Articles.Find(slug);
            if (row is null)
            {
                if (files.ReadMarkdown(slug) is not null)
                    logger.LogWarning("Статья {Slug} есть на диске, но её нет в базе", slug);
                return NotFound(pages, db, settings, user);
            }

            if (!files.MarkdownExists(slug)) return NotFound(pages, db, settings, user);

            // Ключ кэша — content_hash из БД, а не отпечаток файла: PUT меняет хэш всегда,
            // даже если mtime и длина файла на диске совпали со старой версией. Отпечаток каталога
            // сбрасывает кэш, когда меняется список статей: иначе дерево на старой странице не заметит.
            var html = cache.GetOrBuild(slug, row.ContentHash, theme.Version, CatalogFingerprint.Of(db), () =>
            {
                var text = files.ReadMarkdown(slug)!;
                var parsed = FrontMatterParser.Parse(text);
                var body = markdown.Render(parsed.Body, slug, articles);

                return pages.Render("article.html", new()
                {
                    ["page_title"] = parsed.FrontMatter.Title ?? slug,
                    ["description"] = parsed.FrontMatter.Description,
                    ["site"] = SiteModel(settings),
                    ["article"] = new Dictionary<string, object?>
                    {
                        ["slug"] = slug,
                        ["title"] = parsed.FrontMatter.Title ?? slug,
                        ["description"] = parsed.FrontMatter.Description,
                        ["date"] = parsed.FrontMatter.Date?.ToString("yyyy-MM-dd"),
                        ["html"] = body,
                    },
                    ["nav"] = Navigation(db, currentSlug: slug),
                    ["user"] = UserModel(user),
                });
            });

            return Results.Content(html, "text/html; charset=utf-8");
        });

        group.MapGet("/{slug}/{*file}", (string slug, string file, ArticleFiles files, PageRenderer pages,
                                         SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user) =>
        {
            var path = files.AttachmentPath(slug, file);
            if (path is null) return NotFound(pages, db, settings, user);

            var type = ContentTypes.TryGetContentType(path, out var found) ? found : "application/octet-stream";
            return Results.File(path, type);
        });

        // Вне группы и без авторизации: css нужен странице входа.
        app.MapGet("/assets/{*file}", (string file, IThemeSource theme) =>
        {
            var stream = theme.OpenRead($"assets/{file}");
            if (stream is null) return Results.NotFound();

            var type = ContentTypes.TryGetContentType(file, out var found) ? found : "application/octet-stream";
            return Results.Stream(stream, type);
        }).AllowAnonymous();

        return group;
    }

    static IResult NotFound(PageRenderer pages, SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user)
        => Results.Content(pages.Render("404.html", new()
        {
            ["page_title"] = "Не найдено",
            ["site"] = SiteModel(settings),
            ["nav"] = Navigation(db, currentSlug: null),
            ["user"] = UserModel(user),
        }), "text/html; charset=utf-8", statusCode: 404);

    static Dictionary<string, object?> SiteModel(SiteSettings settings) => new()
    {
        ["title"] = "Samizdat",
        ["color_scheme"] = settings.ColorScheme,
    };

    static Dictionary<string, object?> UserModel(ClaimsPrincipal user) => new()
    {
        ["login"] = user.Identity!.Name,
    };

    static Dictionary<string, object?> Navigation(SamizdatDbContext db, string? currentSlug)
    {
        var entries = db.Articles
            .OrderBy(article => article.Folder).ThenBy(article => article.Title)
            .Select(article => new ArticleEntry(article.Folder, article.Slug, article.Title))
            .ToList();

        return ToModel(ArticleTree.Build(entries, currentSlug));
    }

    static Dictionary<string, object?> ToModel(TreeNode node) => new()
    {
        ["name"] = node.Name,
        ["path"] = node.Path,
        ["has_current"] = node.HasCurrent,
        ["folders"] = node.Folders.Select(ToModel).ToList(),
        ["articles"] = node.Articles.Select(article => new Dictionary<string, object?>
        {
            ["slug"] = article.Slug,
            ["title"] = article.Title,
            ["is_current"] = article.IsCurrent,
        }).ToList(),
    };
}
