using Samizdat.Server.Data;

namespace Samizdat.Server.Rendering;

/// Меняется при любой правке каталога: по нему сбрасываются страницы, на которых нарисовано меню.
public static class CatalogFingerprint
{
    public static string Of(SamizdatDbContext db)
    {
        var count = db.Articles.Count();
        var latest = db.Articles.Max(article => (DateTimeOffset?)article.UpdatedAt);
        // Смена видимости не трогает UpdatedAt: то — время правки текста, по нему считается
        // расхождение с вольтом. Без своего слагаемого закрытая статья осталась бы в кэше.
        var access = db.Articles.Max(article => (DateTimeOffset?)article.VisibilityChangedAt);
        return $"{count}:{latest?.UtcTicks ?? 0}:{access?.UtcTicks ?? 0}";
    }
}
