using System.Reflection;

namespace Samizdat.Core.Localization;

public sealed class EmbeddedLanguageSource : ILanguageSource
{
    const string Prefix = "lang/";
    const string Suffix = ".json";

    static readonly Assembly Owner = typeof(EmbeddedLanguageSource).Assembly;

    public string Version { get; } = Owner.GetName().Version?.ToString() ?? "1";

    public IReadOnlyDictionary<string, string>? Read(string code)
    {
        using var stream = Owner.GetManifestResourceStream($"{Prefix}{code}{Suffix}");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return LanguagePack.Parse(reader.ReadToEnd(), $"{code}{Suffix}");
    }

    public IReadOnlyList<string> Codes()
        => Owner.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal)
                           && name.EndsWith(Suffix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..^Suffix.Length])
            .ToList();
}
