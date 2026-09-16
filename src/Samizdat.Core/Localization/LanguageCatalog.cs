using System.Collections.Concurrent;

namespace Samizdat.Core.Localization;

public readonly record struct Language(string Code, string Name);

/// Пакеты читаются с диска и из сборки, поэтому держатся в кэше и перечитываются только
/// после правки файла — как это делает ThemeFactory с темами.
public sealed class LanguageCatalog(ILanguageSource source)
{
    public const string Fallback = "en";

    readonly ConcurrentDictionary<(string Code, string Version), Translator> cache = new();

    public Translator For(string? code)
    {
        var wanted = string.IsNullOrWhiteSpace(code) ? Fallback : code;
        return cache.GetOrAdd((wanted, source.Version), key => Build(key.Code));
    }

    /// Языки для списка в настройках: код и самоназвание из самого пакета.
    public IReadOnlyList<Language> Available()
        => source.Codes()
            .Select(code => new Language(code, source.Read(code)?.GetValueOrDefault("language.name") ?? code))
            .OrderBy(language => language.Code, StringComparer.Ordinal)
            .ToList();

    public bool Has(string? code) => code is { Length: > 0 } && source.Read(code) is not null;

    Translator Build(string code)
    {
        var pack = source.Read(code);
        var english = code == Fallback ? null : source.Read(Fallback);
        // Нет и такого пакета, и английского — переводчик отдаёт сами ключи: страница остаётся
        // живой, а пропажа видна на ней же.
        return pack is null
            ? new Translator(Fallback, english ?? new Dictionary<string, string>())
            : new Translator(code, pack, english);
    }
}
