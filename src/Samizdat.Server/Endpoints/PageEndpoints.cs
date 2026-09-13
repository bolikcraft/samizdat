using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;
using Samizdat.Core;
using Samizdat.Core.Navigation;
using Samizdat.Core.Rendering;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
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

        group.MapGet("/", (PageRenderer pages, SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                           IAntiforgery antiforgery, HttpContext context) =>
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
                ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
            }), "text/html; charset=utf-8");
        });

        group.MapGet("/{slug}", (string slug, PageRenderer pages, ArticleFiles files,
                                 ArticleRenderer markdown, IArticleLookup articles,
                                 PageCache cache, IThemeSource theme, SamizdatDbContext db,
                                 SiteSettings settings, ClaimsPrincipal user, ILogger<Program> logger,
                                 IAntiforgery antiforgery, HttpContext context) =>
        {
            var row = db.Articles.Find(slug);
            if (row is null)
            {
                if (files.ReadMarkdown(slug) is not null)
                    logger.LogWarning("Статья {Slug} есть на диске, но её нет в базе", slug);
                return NotFound(pages, db, settings, user, antiforgery, context);
            }

            if (!files.MarkdownExists(slug)) return NotFound(pages, db, settings, user, antiforgery, context);

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
                    // Плейсхолдер, не настоящий токен: страница кэшируется по slug и общая для всех
                    // гостей, а токен привязан к cookie конкретной сессии — см. Replace ниже.
                    ["antiforgery"] = AntiforgeryHtml.Placeholder,
                });
            });

            html = html.Replace(AntiforgeryHtml.Placeholder, AntiforgeryHtml.Field(antiforgery, context));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        group.MapGet("/{slug}/{*file}", (string slug, string file, ArticleFiles files, PageRenderer pages,
                                         SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                                         IAntiforgery antiforgery, HttpContext context) =>
        {
            var path = files.AttachmentPath(slug, file);
            if (path is null) return NotFound(pages, db, settings, user, antiforgery, context);

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

        // Отдельный путь, не под /assets/: там уже стоит маршрут файлов темы.
        // Гостю по share-ссылке фон тоже нужен, поэтому без пароля.
        app.MapGet("/background", (SiteSettings settings, BackgroundFile background) =>
        {
            try
            {
                if (background.Open(settings.BackgroundFileName) is not { } found) return Results.NotFound();

                return Results.Stream(found.Content, found.ContentType,
                    lastModified: found.LastWrite,
                    entityTag: new EntityTagHeaderValue($"\"{found.LastWrite.Ticks}\""));
            }
            catch (IOException)
            {
                // Файл могли снести между File.Exists и File.OpenRead внутри Open — заменой фона
                // или ручной чисткой. FileNotFoundException — тоже IOException, ловим оба случая.
                return Results.NotFound();
            }
        }).AllowAnonymous();

        return group;
    }

    static IResult NotFound(PageRenderer pages, SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                            IAntiforgery antiforgery, HttpContext context)
        => Results.Content(pages.Render("404.html", new()
        {
            ["page_title"] = "Не найдено",
            ["site"] = SiteModel(settings),
            ["nav"] = Navigation(db, currentSlug: null),
            ["user"] = UserModel(user),
            ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
        }), "text/html; charset=utf-8", statusCode: 404);

    internal static Dictionary<string, object?> SiteModel(SiteSettings settings) => new()
    {
        ["title"] = "Samizdat",
        ["color_scheme"] = settings.ColorScheme,
        ["background_url"] = settings.BackgroundUrl,
    };

    internal static Dictionary<string, object?> UserModel(ClaimsPrincipal user) => new()
    {
        ["login"] = user.Identity!.Name,
    };

    internal static Dictionary<string, object?> Navigation(SamizdatDbContext db, string? currentSlug)
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
