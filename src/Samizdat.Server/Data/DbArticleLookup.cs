using System.Diagnostics.CodeAnalysis;
using Samizdat.Core.Rendering;

namespace Samizdat.Server.Data;

// isOwner решает то же, что as_link в дереве и на главной: читателю закрытая статья не должна
// стать ссылкой, даже если статья, где стоит [[вики-ссылка]], сама открыта.
public sealed class DbArticleLookup(SamizdatDbContext db, bool isOwner) : IArticleLookup
{
    // Список берётся один раз на запрос: в статье десятки ссылок, и запрос на каждую был бы
    // десятками запросов в базу на один рендер.
    // Кэш живёт до конца scope и не сбрасывается: не резолвить после записи статьи в том же
    // scope, иначе свежая статья сюда не попадёт.
    HashSet<string>? slugs;
    Dictionary<string, string>? names;

    public string? Resolve(string target)
    {
        if (slugs is null || names is null) Load();

        var candidates = WikiLinkTarget.Candidates(target);
        // Сначала slug по всем кандидатам: ссылка на явный slug не должна уйти к заметке,
        // у которой так называется файл.
        foreach (var candidate in candidates)
            if (slugs.Contains(candidate)) return candidate;
        foreach (var candidate in candidates)
            if (names.TryGetValue(candidate, out var slug)) return slug;
        return null;
    }

    [MemberNotNull(nameof(slugs), nameof(names))]
    void Load()
    {
        var visible = db.Articles
            .Where(article => isOwner || article.Visibility == ArticleVisibility.Shared)
            .Select(article => new { article.Slug, article.NoteName })
            .ToList();

        slugs = visible.Select(article => article.Slug).ToHashSet(StringComparer.Ordinal);
        // Одно имя бывает у заметок из разных папок. Меньший slug выбираем, чтобы цель ссылки
        // не менялась от запроса к запросу.
        names = visible
            .Where(article => article.NoteName is not null)
            .GroupBy(article => article.NoteName!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                          group => group.Select(article => article.Slug).Order(StringComparer.Ordinal).First(),
                          StringComparer.Ordinal);
    }
}
