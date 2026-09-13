using Samizdat.Core.Rendering;

namespace Samizdat.Server.Data;

public sealed class DbArticleLookup(SamizdatDbContext db) : IArticleLookup
{
    public bool Exists(string slug) => db.Articles.Any(article => article.Slug == slug);
}
