using Microsoft.EntityFrameworkCore;
using Samizdat.Core;
using Samizdat.Server.Data;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Search;

/// Достраивает индекс статьям, выложенным до этого этапа, и по команде пересобирает его целиком.
public static class IndexBackfill
{
    /// force — считать заново всё, не глядя на IndexedHash: нужно, когда поменялся сам разбор.
    public static int Run(SamizdatDbContext db, ArticleFiles files, ILogger logger, bool force)
    {
        var rows = force
            ? db.Articles.ToList()
            : db.Articles.Where(article => article.IndexedHash != article.ContentHash).ToList();
        if (rows.Count == 0) return 0;

        var indexer = new ArticleIndexer(db);
        var done = 0;

        foreach (var row in rows)
        {
            var text = files.ReadMarkdown(row.Slug);
            if (text is null)
            {
                logger.LogWarning("Статья {Slug} есть в базе, но не на диске: индекс останется пустым",
                                  row.Slug);
                continue;
            }

            try
            {
                indexer.Index(row, FrontMatterParser.Parse(text).Body);
                done++;
            }
            catch (FrontMatterException error)
            {
                logger.LogWarning(error, "Статью {Slug} не удалось разобрать: индекс останется пустым",
                                  row.Slug);
            }
        }

        // Упавший старт хуже поиска без одной статьи, поэтому отказ записи только пишем в лог.
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException error)
        {
            logger.LogError(error, "Индекс не записан: статьи останутся без поиска");
            return 0;
        }

        return done;
    }
}
