using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Samizdat.Core;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Search;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class ApiEndpoints
{
    // Пространство ключей для блокировки на слаг (2-арг форма pg_advisory_xact_lock) — отдельное
    // от других блокировок вроде QueueLock регистрации, чтобы хэш слага не мог задеть чужую.
    const int SlugLockNamespace = 587_240_119;

    /// Слаг статьи заперт до конца транзакции: параллельный PUT/DELETE того же слага иначе
    /// делит один каталог на диске (BeginReplace двигает его в .old-/.tmp-) и один из двух
    /// спотыкается о то, что другой уже подвинул. Блокировка транзакционная — снимается сама
    /// на Commit/Rollback и видна всем процессам (reindex, вторая копия сервера), а не только
    /// этому запросу.
    static Task LockSlug(SamizdatDbContext db, string slug)
        => db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({SlugLockNamespace}, hashtext({slug}))");

    public static void MapApi(this WebApplication app)
    {
        // Bearer-токен, не cookie: antiforgery здесь неприменим в принципе, отключаем на всю группу
        // разом, чтобы не забыть про новые маршруты.
        var api = app.MapGroup("/api")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(ApiToken.Scheme)
                .RequireRole(nameof(UserRole.Owner)))
            .DisableAntiforgery();

        api.MapGet("/state", (SamizdatDbContext db) =>
            Results.Ok(db.Articles.ToDictionary(article => article.Slug, article => article.ContentHash)));

        api.MapPut("/articles/{slug}", async (string slug, HttpRequest request,
                                              ArticleFiles files, SamizdatDbContext db,
                                              ArticleIndexer indexer, ILogger<Program> logger) =>
        {
            if (!ArticleFiles.IsValidSlug(slug)) return Results.BadRequest("Плохой slug");
            if (ArticleFiles.IsReservedSlug(slug))
                return Results.BadRequest($"{slug}: адрес занят служебным маршрутом, задайте другой slug");

            var form = await request.ReadFormAsync();
            var folder = form["folder"].ToString();
            if (!ArticleFiles.IsValidFolder(folder)) return Results.BadRequest($"{slug}: плохая папка");

            var source = form.Files.GetFile("index.md");
            if (source is null) return Results.BadRequest("Нет файла index.md");

            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            var markdown = buffer.ToArray();

            var attachments = new List<(string Name, byte[] Bytes)>();
            foreach (var file in form.Files.Where(file => file.Name == "attachments"))
            {
                using var one = new MemoryStream();
                await file.CopyToAsync(one);
                attachments.Add((Path.GetFileName(file.FileName), one.ToArray()));
            }

            ParsedDocument parsed;
            try
            {
                parsed = FrontMatterParser.Parse(Encoding.UTF8.GetString(markdown));
            }
            catch (FrontMatterException error)
            {
                return Results.BadRequest($"{slug}/index.md: {error.Message}");
            }

            await using var transaction = await db.Database.BeginTransactionAsync();
            await LockSlug(db, slug);

            // Запись на диск можно отменить: SaveChangesAsync ниже способен отказать уже после того,
            // как файлы легли на место, и тогда диск и база разойдутся.
            var write = files.BeginReplace(slug, markdown, attachments);

            var row = await db.Articles.FindAsync(slug) ?? db.Articles.Add(new ArticleRow
            {
                Slug = slug, Title = slug, ContentHash = "",
            }).Entity;

            row.Title = parsed.FrontMatter.Title ?? slug;
            row.Folder = folder;
            row.Description = parsed.FrontMatter.Description;
            row.Date = parsed.FrontMatter.Date;
            row.Theme = parsed.FrontMatter.Theme;
            row.ContentHash = ArticleHash.Compute(markdown, attachments, folder);
            row.UpdatedAt = DateTimeOffset.UtcNow;

            // Сам откат может отказать (площадка потеряла право записи, диск отвалился). Тогда файлы
            // на диске так и останутся отвергнутой базой версией — но об этом должен узнать владелец
            // через лог, а не вместо ответа: подмена собой исходной причины отказа только запутает.
            void RollbackFileWrite()
            {
                try
                {
                    write.Rollback();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(error, "Статья {Slug}: не удалось откатить запись на диске, " +
                                            "там осталась версия, отвергнутая базой", slug);
                }
            }

            try
            {
                indexer.Index(row, parsed.Body);
                await db.SaveChangesAsync();
            }
            // Описание из фронтматтера ничем не ограничено, а у tsvector предел 1 МБ на документ:
            // без этого владелец получил бы на выкладке голую пятисотку.
            catch (DbUpdateException error) when (error.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.ProgramLimitExceeded })
            {
                RollbackFileWrite();
                await transaction.RollbackAsync();
                return Results.BadRequest($"{slug}: описание слишком длинное для поискового индекса");
            }
            catch
            {
                RollbackFileWrite();
                await transaction.RollbackAsync();
                throw;
            }

            write.Commit();
            await transaction.CommitAsync();
            return Results.Ok(new { slug, hash = row.ContentHash });
        });

        api.MapDelete("/articles/{slug}", async (string slug, ArticleFiles files, SamizdatDbContext db,
                                                 ILogger<Program> logger) =>
        {
            if (!ArticleFiles.IsValidSlug(slug)) return Results.BadRequest("Плохой slug");

            await using var transaction = await db.Database.BeginTransactionAsync();
            await LockSlug(db, slug);

            // Строка и файл — одна транзакция: откажет удаление с диска, откатится и удаление
            // строки. Итог такого отказа — «строка без файла» (лог ниже плюс предупреждение
            // IndexBackfill при следующем старте), а не тихий мусор, как было бы у «файла без
            // строки». Удаление с диска остаётся под той же блокировкой: конкурентный PUT ждёт
            // её снятия, а не застаёт каталог на полпути.
            var row = await db.Articles.FindAsync(slug);
            if (row is not null)
            {
                db.Articles.Remove(row);
                await db.SaveChangesAsync();
            }
            try
            {
                files.Remove(slug);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger.LogError(error, "Статья {Slug}: не удалось удалить файлы с диска", slug);
                throw;
            }
            await transaction.CommitAsync();
            return Results.Ok();
        });

        api.MapGet("/articles/{slug}.md", (string slug, ArticleFiles files) =>
        {
            if (!ArticleFiles.IsValidSlug(slug)) return Results.BadRequest("Плохой slug");

            var text = files.ReadMarkdown(slug);
            return text is null
                ? Results.NotFound()
                : Results.Text(text, "text/markdown; charset=utf-8");
        });
    }
}
