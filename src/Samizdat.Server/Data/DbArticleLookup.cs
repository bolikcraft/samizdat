using Samizdat.Core.Rendering;

namespace Samizdat.Server.Data;

public sealed class DbArticleLookup(SamizdatDbContext db) : IArticleLookup
{
    // Список берётся один раз на запрос: в статье десятки ссылок, и запрос на каждую был бы
    // десятками запросов в базу на один рендер.
    // Кэш живёт до конца scope и не сбрасывается: не резолвить после записи статьи в том же
    // scope, иначе свежая статья сюда не попадёт.
    HashSet<string>? slugs;

    public string? Resolve(string target)
    {
        slugs ??= db.Articles.Select(article => article.Slug).ToHashSet(StringComparer.Ordinal);
        return WikiLinkTarget.Candidates(target).FirstOrDefault(slugs.Contains);
    }
}
