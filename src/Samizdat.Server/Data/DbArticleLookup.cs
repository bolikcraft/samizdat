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

    public string? Resolve(string target)
    {
        slugs ??= db.Articles
            .Where(article => isOwner || article.Visibility == ArticleVisibility.Shared)
            .Select(article => article.Slug)
            .ToHashSet(StringComparer.Ordinal);
        return WikiLinkTarget.Candidates(target).FirstOrDefault(slugs.Contains);
    }
}
