using System.Collections.Concurrent;
using Samizdat.Core.Themes;

namespace Samizdat.Server.Rendering;

/// Тема выбирается на запрос из настроек в БД, поэтому источник строится по имени и держится
/// в кэше — иначе DiskThemeSource пересчитывал бы дерево файлов на каждый запрос.
public sealed class ThemeFactory(string dataRoot, ILogger<ThemeFactory> logger)
{
    readonly ConcurrentDictionary<string, IThemeSource> cache = new();

    public IThemeSource Get(string name)
    {
        if (name != "default" && !Directory.Exists(Path.Combine(dataRoot, "themes", name)))
        {
            logger.LogWarning("Тема {Theme} не найдена на диске, используется встроенная default", name);
            name = "default";
        }

        return cache.GetOrAdd(name, key => new LayeredThemeSource(
            new DiskThemeSource(Path.Combine(dataRoot, "themes", key)),
            new EmbeddedThemeSource()));
    }

    /// Встроенная "default" плюс подпапки dataRoot/themes — список для выпадающего меню настроек.
    public IReadOnlyList<string> AvailableThemes()
    {
        var names = new List<string> { "default" };
        var themesDir = Path.Combine(dataRoot, "themes");
        if (Directory.Exists(themesDir))
        {
            var extra = Directory.EnumerateDirectories(themesDir)
                .Select(dir => Path.GetFileName(dir)!)
                .Where(name => name != "default")
                .OrderBy(name => name, StringComparer.Ordinal);
            names.AddRange(extra);
        }
        return names;
    }
}
