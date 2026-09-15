using System.Security.Claims;
using System.Text;
using Samizdat.Core;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class DownloadEndpoints
{
    public static void MapDownload(this WebApplication app)
    {
        // Сегмент download занят служебно (см. ArticleFiles.ReservedSlugs), поэтому маршрут не спорит
        // ни с /{slug}, ни с вложением статьи.
        app.MapGet("/download/{slug}", (string slug, ArticleFiles files, SamizdatDbContext db,
                                        SiteSettings settings, ClaimsPrincipal user) =>
        {
            var row = db.Articles.Find(slug);
            if (row is null || !files.MarkdownExists(slug)) return Results.NotFound();

            var role = ArticleAccess.RoleOf(user);
            // Отказ — 404, а не 403: выключенное скачивание не «закрыто», такой функции просто нет.
            if (!ArticleAccess.CanDownload(row.Visibility, role, settings.Download)) return Results.NotFound();

            return Package(files, slug, asIs: role == UserRole.Owner);
        }).RequireAuthorization();

        // Гостевой адрес трёхсегментный, а slug у статьи односегментный — со строкой выше не спорит.
        app.MapGet("/download/s/{token}", (string token, ArticleFiles files, SamizdatDbContext db,
                                           SiteSettings settings, HttpContext context) =>
        {
            // Как и остальные гостевые маршруты: после отзыва ссылки файл не должен лежать в прокси.
            context.Response.Headers.CacheControl = "no-store";

            var link = db.ShareLinks.FirstOrDefault(row => row.Token == token);
            if (link is null || !link.IsAlive(DateTimeOffset.UtcNow)) return Results.NotFound();
            if (!ArticleAccess.CanDownloadByShare(settings.Download)) return Results.NotFound();

            // Slug берётся из ссылки, а не из запроса: по чужой статье этот токен не пройдёт.
            if (!files.MarkdownExists(link.Slug)) return Results.NotFound();

            // Счётчик открытий не трогаем: скачивание — не открытие страницы.
            return Package(files, link.Slug, asIs: false);
        }).AllowAnonymous();
    }

    /// asIs — файл владельцу, байт в байт. Остальным шапка пересобирается.
    internal static IResult Package(ArticleFiles files, string slug, bool asIs)
    {
        byte[] markdown;
        if (asIs)
        {
            markdown = files.ReadMarkdownBytes(slug)!;
        }
        else
        {
            // Сломанный фронтматтер — 404: разобрать шапку нечем, а отдать её как есть нельзя,
            // иначе чужие поля уедут вместе с файлом. Владельцу такой файл по-прежнему отдаётся.
            try
            {
                markdown = Encoding.UTF8.GetBytes(PublicSource.Of(files.ReadMarkdown(slug)!));
            }
            catch (FrontMatterException)
            {
                return Results.NotFound();
            }
        }

        var attachments = files.Attachments(slug).ToList();

        // fileDownloadName сам оформляет Content-Disposition, в том числе кириллицу в имени.
        return attachments.Count == 0
            ? Results.File(markdown, "text/markdown; charset=utf-8", $"{slug}.md")
            : Results.File(ArticlePackage.Pack(slug, markdown, attachments), "application/zip", $"{slug}.zip");
    }
}
