using System.Globalization;

namespace Samizdat.Core.Localization;

/// Строка ищется в выбранном языке, потом в английском, потом отдаётся сам ключ: пропущенный
/// перевод видно прямо на странице.
public sealed class Translator(string code, IReadOnlyDictionary<string, string> pack,
                               IReadOnlyDictionary<string, string>? english = null)
{
    Dictionary<string, object?>? model;

    public string Code => code;

    public string this[string key]
        => pack.GetValueOrDefault(key) ?? english?.GetValueOrDefault(key) ?? key;

    public string Format(string key, params object?[] values)
        => string.Format(CultureInfo.InvariantCulture, this[key], values);

    /// Точечные ключи разворачиваются в дерево: "nav.articles" шаблон читает как t.nav.articles.
    public Dictionary<string, object?> Model() => model ??= BuildModel();

    Dictionary<string, object?> BuildModel()
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in pack.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var parts = key.Split('.');
            var node = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!node.TryGetValue(parts[i], out var next))
                    node[parts[i]] = next = new Dictionary<string, object?>(StringComparer.Ordinal);
                node = next as Dictionary<string, object?>
                       ?? throw new LanguageException($"Ключ {key}: {parts[i]} уже занят строкой");
            }

            var last = parts[^1];
            if (node.TryGetValue(last, out var taken) && taken is Dictionary<string, object?>)
                throw new LanguageException($"Ключ {key}: {last} уже занят ветвью");
            node[last] = value;
        }
        return root;
    }
}
