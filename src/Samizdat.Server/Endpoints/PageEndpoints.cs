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
            var isOwner = ArticleAccess.IsOwner(user);
            var rows = db.Articles.OrderByDescending(article => article.Date).ToList();
            var list = rows.Select(article => new Dictionary<string, object?>
            {
                ["slug"] = article.Slug,
                ["title"] = article.Title,
                ["description"] = article.Description,
                ["date"] = article.Date.HasValue ? article.Date.Value.ToString("yyyy-MM-dd") : null,
                ["is_shared"] = article.Visibility == ArticleVisibility.Shared,
                ["as_link"] = isOwner || article.Visibility == ArticleVisibility.Shared,
            }).ToList();

            return Results.Content(pages.Render("index.html", new()
            {
                ["page_title"] = "Samizdat",
                ["site"] = SiteModel(settings),
                ["articles"] = list,
                ["nav"] = Navigation(db, currentSlug: null, isOwner),
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

            if (!ArticleAccess.CanRead(row.Visibility, ArticleAccess.RoleOf(user)))
                return Forbidden(pages, db, settings, user, antiforgery, context);

            // Content — content_hash из БД, а не отпечаток файла: PUT меняет хэш всегда, даже если
            // mtime и длина файла на диске совпали со старой версией. Catalog сбрасывает кэш, когда
            // меняется список статей: иначе дерево на старой странице этого не заметит.
            var key = new PageKey(Content: row.ContentHash, Theme: theme.Version,
                                  Catalog: CatalogFingerprint.Of(db), View: settings.ViewFingerprint);
            var isOwner = ArticleAccess.IsOwner(user);
            // Своя ячейка, а не роль в ключе: с общей ячейкой владелец и читатель вытесняли бы
            // страницы друг друга, и каждый второй запрос шёл бы в полный рендер.
            var cell = isOwner ? slug : $"reader/{slug}";
            var html = cache.GetOrBuild(cell, key, () =>
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
                        ["is_shared"] = row.Visibility == ArticleVisibility.Shared,
                    },
                    ["nav"] = Navigation(db, currentSlug: slug, isOwner),
                    ["user"] = UserModel(user),
                    // Плейсхолдер, не настоящий токен: страница кэшируется по slug и общая для всех
                    // гостей, а токен привязан к cookie конкретной сессии — см. Replace ниже.
                    ["antiforgery"] = AntiforgeryHtml.Placeholder,
                    // Тем же приёмом: ссылка меняется без правки статьи, в кэше ей не место.
                    ["share_panel"] = SharePanel.Placeholder,
                });
            });

            html = html.Replace(AntiforgeryHtml.Placeholder, AntiforgeryHtml.Field(antiforgery, context));
            html = html.Replace(SharePanel.Placeholder,
                SharePanel.Render(pages, db, slug, antiforgery, context,
                                  ArticleAccess.CanShare(row.Visibility, ArticleAccess.RoleOf(user))));
            return Results.Content(html, "text/html; charset=utf-8");
        });

        group.MapGet("/{slug}/{*file}", (string slug, string file, ArticleFiles files, PageRenderer pages,
                                         SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                                         IAntiforgery antiforgery, HttpContext context) =>
        {
            // Доступ проверяется до выдачи файла, иначе вложения закрытой статьи уходят в обход страницы.
            var row = db.Articles.Find(slug);
            if (row is null) return NotFound(pages, db, settings, user, antiforgery, context);
            if (!ArticleAccess.CanRead(row.Visibility, ArticleAccess.RoleOf(user)))
                return Forbidden(pages, db, settings, user, antiforgery, context);

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
        app.MapGet("/background", (BackgroundFile background, ILogger<Program> logger, HttpContext context) =>
        {
            // Отдаём загруженный файл, а не имя из настроек: в галерее настроек картинка видна
            // плиткой и тогда, когда фоном стоит цвет или картинка из набора.
            var name = background.Current();
            try
            {
                if (name is null || background.Open(name) is not { } found) return Results.NotFound();

                // Адрес несёт ?v=<mtime файла>, значит байты по одному адресу не меняются — кэшировать
                // можно надолго. private, не public: сайт закрыт, общему кэшу перед ним (Апач, KeenDNS,
                // в будущем CDN) картинка не нужна. Не immutable: версия — mtime, а не хэш содержимого,
                // и восстановление фона из бэкапа с сохранением времени оставило бы в браузере старую
                // картинку на год.
                context.Response.Headers.CacheControl = "private, max-age=2592000";

                return Results.Stream(found.Content, found.ContentType,
                    lastModified: found.LastWrite,
                    entityTag: new EntityTagHeaderValue($"\"{found.LastWrite.Ticks}\""));
            }
            catch (IOException error)
            {
                // catch (IOException) — не только гонка File.Exists/File.OpenRead внутри Open (снесли
                // заменой фона или вручную): сюда же попадёт и настоящий сбой диска. Гостю в обоих
                // случаях отвечаем 404, но сбой стоит видеть в логе, а не терять молча.
                logger.LogWarning(error, "Не удалось открыть фон {Name}", name);
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
            ["nav"] = Navigation(db, currentSlug: null, ArticleAccess.IsOwner(user)),
            ["user"] = UserModel(user),
            ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
        }), "text/html; charset=utf-8", statusCode: 404);

    static IResult Forbidden(PageRenderer pages, SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                             IAntiforgery antiforgery, HttpContext context)
        => Results.Content(pages.Render("403.html", new()
        {
            ["page_title"] = "Статья закрыта",
            ["site"] = SiteModel(settings),
            ["nav"] = Navigation(db, currentSlug: null, ArticleAccess.IsOwner(user)),
            ["user"] = UserModel(user),
            ["antiforgery"] = AntiforgeryHtml.Field(antiforgery, context),
        }), "text/html; charset=utf-8", statusCode: 403);

    internal static Dictionary<string, object?> SiteModel(SiteSettings settings) => new()
    {
        ["title"] = "Samizdat",
        ["color_scheme"] = settings.ColorScheme,
        ["background_url"] = settings.BackgroundUrl,
        ["background_color"] = settings.BackgroundColor,
    };

    internal static Dictionary<string, object?> UserModel(ClaimsPrincipal user) => new()
    {
        ["login"] = user.Identity!.Name,
        ["is_owner"] = ArticleAccess.IsOwner(user),
    };

    internal static Dictionary<string, object?> Navigation(SamizdatDbContext db, string? currentSlug,
                                                           bool isOwner = true)
    {
        var entries = db.Articles
            .OrderBy(article => article.Folder).ThenBy(article => article.Title)
            .Select(article => new ArticleEntry(article.Folder, article.Slug, article.Title,
                                                article.Visibility == ArticleVisibility.Shared))
            .ToList();

        return ToModel(ArticleTree.Build(entries, currentSlug), isOwner);
    }

    static Dictionary<string, object?> ToModel(TreeNode node, bool isOwner) => new()
    {
        ["name"] = node.Name,
        ["path"] = node.Path,
        ["has_current"] = node.HasCurrent,
        ["folders"] = node.Folders.Select(folder => ToModel(folder, isOwner)).ToList(),
        ["articles"] = node.Articles.Select(article => new Dictionary<string, object?>
        {
            ["slug"] = article.Slug,
            ["title"] = article.Title,
            ["is_current"] = article.IsCurrent,
            ["is_shared"] = article.IsShared,
            // Ссылку рисуем только на то, что этот зритель откроет: у читателя закрытая статья
            // остаётся строкой с заголовком.
            ["as_link"] = isOwner || article.IsShared,
        }).ToList(),
    };
}
