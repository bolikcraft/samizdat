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

    /// body — текст статьи без фронтматтера.
    public void Index(ArticleRow row, string body)
    {
        var text = PlainText.Extract(body);
        row.SearchText = text.Length > MaxSearchText ? text[..MaxSearchText] : text;
        row.IndexedHash = row.ContentHash;

        // Пишем обе формы цели: рендер ищет сперва буквальный slug, потом транслитерацию имени
        // заметки. Лишняя строка ни с чем не соединится, а без неё бэклинк разошёлся бы со ссылкой.
        var wanted = WikiLinks.Targets(body)
            .SelectMany(WikiLinkTarget.Candidates)
            .Where(target => target != row.Slug)
            .ToHashSet(StringComparer.Ordinal);

        var existing = db.ArticleLinks.Where(link => link.FromSlug == row.Slug).ToList();

        // Строки, что остались на месте, не трогаем: удалить и тут же вставить ту же пару EF
        // не даст — ключ один и тот же.
        foreach (var link in existing.Where(link => !wanted.Contains(link.ToSlug)))
            db.ArticleLinks.Remove(link);

        foreach (var target in wanted.Where(target => existing.All(link => link.ToSlug != target)))
            db.ArticleLinks.Add(new ArticleLinkRow { FromSlug = row.Slug, ToSlug = target });
    }
}
