namespace Samizdat.Core.Localization;

/// Диск поверх встроенного, слияние по ключам: файл на диске может править одну строку,
/// а не нести весь пакет.
public sealed class LayeredLanguageSource(ILanguageSource disk, ILanguageSource embedded) : ILanguageSource
{
    public string Version => $"{disk.Version}|{embedded.Version}";

    public IReadOnlyDictionary<string, string>? Read(string code)
    {
        var top = disk.Read(code);
        var bottom = embedded.Read(code);
        if (top is null) return bottom;
        if (bottom is null) return top;

        var merged = new Dictionary<string, string>(bottom, StringComparer.Ordinal);
        foreach (var (key, value) in top) merged[key] = value;
        return merged;
    }

    public IReadOnlyList<string> Codes() => disk.Codes().Union(embedded.Codes(), StringComparer.Ordinal).ToList();
}
