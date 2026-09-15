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

    /// Длиннее поисковая строка человеку не нужна, а база на огромном запросе отвечает отказом.
    public const int MaxQuery = 200;

    const int SimilarLimit = 10;

    /// Порог похожести триграмм. Ловит замену и вставку буквы; перестановка соседних букв в коротком
    /// слове не ловится и на этом пороге. Ниже — в находки лезет случайный шум.
    const double SimilarFloor = 0.4;

    public Task<IReadOnlyList<SearchHit>> Find(string query, bool isOwner)
    {
        var text = Clean(query);
        if (text.Length == 0) return Empty;

        // ts_headline молча вырезает из цитаты всё, что похоже на html-тег ("<script>" и подобное,
        // символы `<>&"'`) — это поведение самой Postgres, отключить нельзя (баг #15277 их трекера).
        // Поэтому перед вызовом текст экранируем, а после — экранирование снимаем в обратном
        // порядке: наружу уходит тот же необработанный текст, что и раньше, только без выпавших кусков.
        const string sql = """
            WITH q AS (SELECT websearch_to_tsquery('russian', @query) AS query),
                 -- Цитата строится по описанию и тексту вместе, иначе находка только по описанию
                 -- показывала бы случайный кусок текста без искомого слова. Без описания разделитель
                 -- не добавляем — иначе он лёг бы мусором перед цитатой из текста.
                 source AS (
                     SELECT a."Slug",
                            CASE WHEN a."Description" IS NULL OR a."Description" = '' THEN a."SearchText"
                                 ELSE a."Description" || E'\n\n' || a."SearchText"
                            END AS text
                     FROM articles a
                 ),
                 headline AS (
                     SELECT source."Slug", source.text,
                            replace(replace(replace(replace(replace(
                                ts_headline('russian',
                                    replace(replace(replace(replace(replace(source.text,
                                        '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), '"', '&quot;'), '''', '&#39;'),
                                    q.query, @options),
                                '&#39;', ''''), '&quot;', '"'), '&gt;', '>'), '&lt;', '<'), '&amp;', '&') AS snippet
                     FROM source, q
                 ),
                 -- Многоточие ставим, только если ts_headline реально отрезал край: сравниваем начало
                 -- и конец цитаты без маркеров подсветки с началом и концом источника.
                 edges AS (
                     SELECT headline."Slug", headline.text, headline.snippet,
                            replace(replace(headline.snippet, @mark0, ''), @mark1, '') AS plain
                     FROM headline
                 )
            SELECT a."Slug", a."Title", a."Folder",
                   (a."Visibility" = @shared OR @owner) AS can_open,
                   CASE WHEN a."Visibility" = @shared OR @owner THEN
                       (CASE WHEN strpos(edges.text, split_part(edges.plain, @delimiter, 1)) = 1
                             THEN '' ELSE @ellipsis || ' ' END)
                       || edges.snippet ||
                       (CASE WHEN right(edges.text, char_length(split_part(edges.plain, @delimiter, -1)))
                                  = split_part(edges.plain, @delimiter, -1)
                             THEN '' ELSE ' ' || @ellipsis END)
                       END AS snippet,
                   ts_rank_cd(CASE WHEN a."Visibility" = @shared OR @owner
                                   THEN a."SearchVector" ELSE a."MetaVector" END, q.query) AS rank
            FROM articles a
            JOIN edges ON edges."Slug" = a."Slug"
            CROSS JOIN q
            WHERE ((a."Visibility" = @shared OR @owner) AND a."SearchVector" @@ q.query)
               OR a."MetaVector" @@ q.query
            ORDER BY rank DESC, a."Title"
            LIMIT @limit
            """;

        return Run(sql, ("query", text), ("owner", isOwner), ("shared", (int)ArticleVisibility.Shared),
                   ("options", SearchSnippet.Options), ("limit", Limit),
                   ("mark0", SearchSnippet.Start.ToString()), ("mark1", SearchSnippet.Stop.ToString()),
                   ("delimiter", SearchSnippet.FragmentDelimiter), ("ellipsis", SearchSnippet.Ellipsis));
    }

    /// Запасной путь: полнотекст не нашёл ничего, ищем похожие куски слов. Индексов у триграмм нет
    /// намеренно — путь редкий, а лишний индекс дорожает на каждой выкладке.
    public Task<IReadOnlyList<SearchHit>> FindSimilar(string query, bool isOwner)
    {
        var text = Clean(query);
        if (text.Length == 0) return Empty;

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

        return Run(sql, ("query", text), ("owner", isOwner), ("shared", (int)ArticleVisibility.Shared),
                   ("floor", SimilarFloor), ("limit", SimilarLimit));
    }

    static Task<IReadOnlyList<SearchHit>> Empty => Task.FromResult<IReadOnlyList<SearchHit>>([]);

    /// Строку запроса чистим до похода в базу: нулевой байт база не принимает вовсе, а на слишком
    /// длинном запросе отвечает отказом, потратив на него секунды.
    static string Clean(string query)
    {
        var text = new string(query.Where(symbol => !char.IsControl(symbol)).ToArray()).Trim();
        return TextTrim.Cut(text, MaxQuery);
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
