using Samizdat.Core.Rendering;
using Samizdat.Server.Data;

namespace Samizdat.Server.Search;

/// Пишет поисковый текст статьи и её исходящие ссылки. Сохраняет вызывающий — индекс должен
/// лечь в ту же транзакцию, что и метаданные.
public sealed class ArticleIndexer(SamizdatDbContext db)
{
    /// Предел tsvector — 1 МБ на документ, в кириллице это около полумиллиона знаков. Режем
    /// с запасом: длинная статья должна выложиться, потеряв хвост индекса, а не упасть отказом.
    public const int MaxSearchText = 400_000;

    /// Предел slug статьи — у цели ссылки та же колонка varchar(200).
    public const int MaxSlug = 200;

    /// body — текст статьи без фронтматтера.
    public void Index(ArticleRow row, string body)
    {
        // Заголовок и описание чистим здесь же: они идут в поисковые векторы и на страницу находок.
        row.Title = PlainText.WithoutControls(row.Title);
        row.Description = row.Description is null ? null : PlainText.WithoutControls(row.Description);
        row.SearchText = Trim(PlainText.WithoutControls(PlainText.Extract(body)));
        row.IndexedHash = row.ContentHash;

        // Пишем обе формы цели: рендер ищет сперва буквальный slug, потом транслитерацию имени
        // заметки. Лишняя строка ни с чем не соединится, а без неё бэклинк разошёлся бы со ссылкой.
        // Ссылку на саму себя отбрасываем целиком: статью находит любой из её кандидатов.
        var wanted = WikiLinks.Targets(body)
            .Select(WikiLinkTarget.Candidates)
            .Where(candidates => !candidates.Contains(row.Slug, StringComparer.Ordinal))
            .SelectMany(candidates => candidates)
            // Цель длиннее slug молча отбрасываем: совпасть ей всё равно не с чем, а в колонку
            // она не влезает и валит выкладку отказом базы.
            .Where(target => target.Length <= MaxSlug)
            .ToHashSet(StringComparer.Ordinal);

        var existing = db.ArticleLinks.Where(link => link.FromSlug == row.Slug).ToList();

        // Строки, что остались на месте, не трогаем: удалить и тут же вставить ту же пару EF
        // не даст — ключ один и тот же.
        foreach (var link in existing.Where(link => !wanted.Contains(link.ToSlug)))
            db.ArticleLinks.Remove(link);

        foreach (var target in wanted.Where(target => existing.All(link => link.ToSlug != target)))
            db.ArticleLinks.Add(new ArticleLinkRow { FromSlug = row.Slug, ToSlug = target });
    }

    static string Trim(string text)
    {
        if (text.Length <= MaxSearchText) return text;

        // Половина суррогатной пары на конце — уже не текст: драйвер не переводит её в UTF-8
        // и валит выкладку.
        var end = char.IsHighSurrogate(text[MaxSearchText - 1]) ? MaxSearchText - 1 : MaxSearchText;
        return text[..end];
    }
}
