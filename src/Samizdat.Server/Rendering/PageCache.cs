using System.Collections.Concurrent;

namespace Samizdat.Server.Rendering;

public sealed class PageCache
{
    readonly ConcurrentDictionary<string, (string Key, string Html)> pages = new();

    public string GetOrBuild(string slug, string version, string themeVersion, string catalogFingerprint,
                              Func<string> build)
    {
        var key = $"{version}|{themeVersion}|{catalogFingerprint}";
        if (pages.TryGetValue(slug, out var found) && found.Key == key)
            return found.Html;

        var html = build();
        pages[slug] = (key, html);
        return html;
    }
}
