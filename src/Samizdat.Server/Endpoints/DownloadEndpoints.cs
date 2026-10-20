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
        app.MapGet("/download/{slug}", (string slug, ArticleFiles files, ArticlePackage package,
                                        SamizdatDbContext db, SiteSettings settings, ClaimsPrincipal user,
                                        ZipGate gate, HttpContext context) =>
        {
            var row = db.Articles.Find(slug);
            if (row is null || !files.MarkdownExists(slug)) return Results.NotFound();

            var role = ArticleAccess.RoleOf(user);
            // Отказ — 404, а не 403: выключенное скачивание не «закрыто», такой функции просто нет.
            if (!ArticleAccess.CanDownload(row.Visibility, role, settings.Download)) return Results.NotFound();

            return Package(files, package, gate, context, slug, asIs: role == UserRole.Owner);
        }).RequireAuthorization();

        // Гостевой адрес трёхсегментный, а slug у статьи односегментный — со строкой выше не спорит.
        app.MapGet("/download/s/{token}", (string token, ArticleFiles files, ArticlePackage package,
                                           SamizdatDbContext db, SiteSettings settings, ZipGate gate,
                                           HttpContext context) =>
        {
            // Как и остальные гостевые маршруты: после отзыва ссылки файл не должен лежать в прокси.
            context.Response.Headers.CacheControl = "no-store";

            var link = db.ShareLinks.FirstOrDefault(row => row.Token == token);
            if (link is null || !link.IsAlive(DateTimeOffset.UtcNow)) return Results.NotFound();
            if (!ArticleAccess.CanDownloadByShare(settings.Download)) return Results.NotFound();

            // Slug берётся из ссылки, а не из запроса: по чужой статье этот токен не пройдёт.
            if (!files.MarkdownExists(link.Slug)) return Results.NotFound();

            // Счётчик открытий не трогаем: скачивание — не открытие страницы.
            return Package(files, package, gate, context, link.Slug, asIs: false);
        }).AllowAnonymous();
    }

    /// asIs — файл владельцу, байт в байт. Остальным шапка пересобирается.
    static IResult Package(ArticleFiles files, ArticlePackage package, ZipGate gate, HttpContext context,
                           string slug, bool asIs)
    {
        try
        {
            byte[] markdown;
            if (asIs)
            {
                markdown = files.ReadMarkdownBytes(slug) ?? throw new FileNotFoundException();
            }
            else
            {
                // Сломанный фронтматтер — 404: разобрать шапку нечем, а отдать её как есть нельзя,
                // иначе чужие поля уедут вместе с файлом. Владельцу такой файл по-прежнему отдаётся.
                try
                {
                    var text = files.ReadMarkdown(slug) ?? throw new FileNotFoundException();
                    markdown = Encoding.UTF8.GetBytes(PublicSource.Of(text));
                }
                catch (FrontMatterException)
                {
                    return Results.NotFound();
                }
            }

            var attachments = files.Attachments(slug).ToList();

            // fileDownloadName сам оформляет Content-Disposition, в том числе кириллицу в имени.
            if (attachments.Count == 0)
                return Results.File(markdown, "text/markdown; charset=utf-8", $"{slug}.md");

            // Место держим только на сборку: отдача готового файла его не занимает.
            using var lease = gate.TryEnter();
            if (!lease.IsAcquired)
            {
                context.Response.Headers.RetryAfter = "5";
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            // Results.File закрывает поток после отправки, и временный файл удаляется сам.
            return Results.File(package.Pack(slug, markdown, attachments), "application/zip", $"{slug}.zip");
        }
        catch (IOException)
        {
            // Гонка с Replace: пока собираем файл, владелец выложил новую версию из Obsidian, и
            // Directory.Move увёл папку статьи из-под чтения — это штатный режим, а не сбой, и
            // отдельного предупреждения в лог не пишем (в отличие от /background, где та же гонка —
            // редкое ручное действие и стоит того, чтобы её видеть). FileNotFoundException выше —
            // тот же случай, только пойман раньше, до обращения к несуществующему файлу.
            return Results.NotFound();
        }
    }
}
