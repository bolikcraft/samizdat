using Microsoft.EntityFrameworkCore;
using Samizdat.Core;
using Samizdat.Server.Data;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Search;

/// Сколько статей проиндексировано и сколько не вышло.
public readonly record struct IndexBackfillResult(int Done, int Failed);

/// Достраивает индекс статьям, выложенным до этого этапа, и по команде пересобирает его целиком.
public static class IndexBackfill
{
    /// force — считать заново всё, не глядя на IndexedHash: нужно, когда поменялся сам разбор.
    public static IndexBackfillResult Run(IServiceProvider services, ArticleFiles files, ILogger logger, bool force)
    {
        List<string> slugs;
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            slugs = (force ? db.Articles : db.Articles.Where(row => row.IndexedHash != row.ContentHash))
                .Select(row => row.Slug).ToList();
        }

        var done = 0;
        var failed = 0;
        foreach (var slug in slugs)
        {
            if (Index(services, files, logger, slug)) done++;
            else failed++;
        }

        // Reindex — отдельный процесс: сервер узнаёт о переписанных ссылках только из базы.
        // Метка входит в CatalogFingerprint, иначе закэшированный блок «Упоминается в» не заметит,
        // что кто-то поправил статью мимо PUT.
        if (done > 0)
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteSettings>()
                .Set("index.stamp", DateTimeOffset.UtcNow.Ticks.ToString());
        }

        return new IndexBackfillResult(done, failed);
    }

    /// Своя единица работы на статью: отказ записи на одной не должен оставить без индекса соседей.
    static bool Index(IServiceProvider services, ArticleFiles files, ILogger logger, string slug)
    {
        var text = files.ReadMarkdown(slug);
        if (text is null)
        {
            logger.LogWarning("Статья {Slug} есть в базе, но не на диске: индекс останется пустым", slug);
            return false;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        // Статью могли снести, пока шла достройка.
        var row = db.Articles.FirstOrDefault(article => article.Slug == slug);
        if (row is null) return false;

        try
        {
            new ArticleIndexer(db).Index(row, FrontMatterParser.Parse(text).Body);
            db.SaveChanges();
            return true;
        }
        catch (FrontMatterException error)
        {
            logger.LogWarning(error, "Статью {Slug} не удалось разобрать: индекс останется пустым", slug);
            return false;
        }
        // Сервер должен подняться и с неполным индексом: упавший старт хуже поиска без одной статьи.
        catch (DbUpdateException error)
        {
            logger.LogError(error, "Индекс статьи {Slug} не записан", slug);
            return false;
        }
    }
}
