namespace Samizdat.Core.Localization;

public sealed class DiskLanguageSource(string root) : ILanguageSource
{
    public string Version => LastWrite().ToString("O");

    public IReadOnlyDictionary<string, string>? Read(string code)
    {
        var full = Resolve(code);
        return full is null ? null : LanguagePack.Parse(File.ReadAllText(full), Path.GetFileName(full));
    }

    public IReadOnlyList<string> Codes()
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.json")
                .Select(file => Path.GetFileNameWithoutExtension(file)!)
                .Where(code => Resolve(code) is not null)
                .ToList()
            : [];

    DateTime LastWrite()
    {
        if (!Directory.Exists(root)) return DateTime.MinValue;
        var newest = DateTime.MinValue;
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (written > newest) newest = written;
        }
        return newest;
    }

    /// Защита от выхода за каталог: текстом ("../../etc/passwd") или через симлинк на чужой файл.
    /// Повторяет DiskThemeSource.Resolve.
    string? Resolve(string code)
    {
        var full = Path.GetFullPath(Path.Combine(root, $"{code}.json"));
        var rooted = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rooted, StringComparison.Ordinal) || !File.Exists(full)) return null;

        var target = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
        if (target is not null && !target.StartsWith(rooted, StringComparison.Ordinal)) return null;

        return full;
    }
}
