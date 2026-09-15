using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Samizdat.Server.Data;

namespace Samizdat.Server.Search;

/// Находка: slug, заголовок, папка вольта, можно ли её открыть этому зрителю и цитата (null,
/// если статья ему закрыта и совпал только заголовок).
public sealed record SearchHit(string Slug, string Title, string Folder, bool CanOpen, string? Snippet);

/// Поиск по статьям. Запрос собирается из констант, значения идут только параметрами.
public sealed class ArticleSearch(SamizdatDbContext db)
{
    public const int Limit = 50;
    const int SimilarLimit = 10;

    /// Порог похожести триграмм. Ниже — в находки лезет случайный шум.
    const double SimilarFloor = 0.5;

    public Task<IReadOnlyList<SearchHit>> Find(string query, bool isOwner)
    {
        const string sql = """
            WITH q AS (SELECT websearch_to_tsquery('russian', @query) AS query)
            SELECT a."Slug", a."Title", a."Folder",
                   (a."Visibility" = @shared OR @owner) AS can_open,
                   CASE WHEN a."Visibility" = @shared OR @owner
                        THEN ts_headline('russian', a."SearchText", q.query, @options) END AS snippet,
                   ts_rank_cd(CASE WHEN a."Visibility" = @shared OR @owner
                                   THEN a."SearchVector" ELSE a."MetaVector" END, q.query) AS rank
            FROM articles a, q
            WHERE ((a."Visibility" = @shared OR @owner) AND a."SearchVector" @@ q.query)
               OR a."MetaVector" @@ q.query
            ORDER BY rank DESC, a."Title"
            LIMIT @limit
            """;

        return Run(sql, ("query", query), ("owner", isOwner), ("shared", (int)ArticleVisibility.Shared),
                   ("options", SearchSnippet.Options), ("limit", Limit));
    }

    /// Запасной путь: полнотекст не нашёл ничего, ищем похожие куски слов. Индексов у триграмм нет
    /// намеренно — путь редкий, а лишний индекс дорожает на каждой выкладке.
    public Task<IReadOnlyList<SearchHit>> FindSimilar(string query, bool isOwner)
    {
        // Текст закрытой статьи читателю не сравниваем вовсе: похожесть по нему — та же утечка.
        const string sql = """
            WITH s AS (
                SELECT a."Slug", a."Title", a."Folder", (a."Visibility" = @shared OR @owner) AS can_open,
                       greatest(word_similarity(@query, a."Title"),
                                CASE WHEN a."Visibility" = @shared OR @owner
                                     THEN word_similarity(@query, a."SearchText") ELSE 0 END) AS rank
                FROM articles a)
            SELECT s."Slug", s."Title", s."Folder", s.can_open, NULL::text AS snippet, s.rank
            FROM s
            WHERE s.rank >= @floor
            ORDER BY s.rank DESC, s."Title"
            LIMIT @limit
            """;

        return Run(sql, ("query", query), ("owner", isOwner), ("shared", (int)ArticleVisibility.Shared),
                   ("floor", SimilarFloor), ("limit", SimilarLimit));
    }

    async Task<IReadOnlyList<SearchHit>> Run(string sql, params (string Name, object Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) Add(command, name, value);

            var hits = new List<SearchHit>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                hits.Add(new SearchHit(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return hits;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
