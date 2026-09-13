using Samizdat.Server.Data;

namespace Samizdat.Server.Rendering;

/// Меняется при любой правке каталога: по нему сбрасываются страницы, на которых нарисовано меню.
public static class CatalogFingerprint
{
    public static string Of(SamizdatDbContext db)
    {
        var count = db.Articles.Count();
        var latest = db.Articles.Max(article => (DateTimeOffset?)article.UpdatedAt);
        return $"{count}:{latest?.UtcTicks ?? 0}";
    }
}
