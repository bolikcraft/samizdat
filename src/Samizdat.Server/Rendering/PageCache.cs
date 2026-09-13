using System.Collections.Concurrent;

namespace Samizdat.Server.Rendering;

/// Части ключа лежат отдельными полями, а не склеены в строку через разделитель: разделитель
/// уже встречается внутри полей (IThemeSource.Version сам склеен из версий слоёв), и склейка
/// рано или поздно даст два разных набора с одинаковым ключом.
public readonly record struct PageKey(string Content, string Theme, string Catalog, string View);

public sealed class PageCache
{
    readonly ConcurrentDictionary<string, (PageKey Key, string Html)> pages = new();

    public string GetOrBuild(string slug, PageKey key, Func<string> build)
    {
        if (pages.TryGetValue(slug, out var found) && found.Key == key)
            return found.Html;

        var html = build();
        pages[slug] = (key, html);
        return html;
    }
}
