namespace Samizdat.Core.Themes;

public sealed class DiskThemeSource(string root) : IThemeSource
{
    public string Version => LastWrite().ToString("O");

    public Stream? OpenRead(string path)
    {
        var full = Resolve(path);
        return full is null ? null : File.OpenRead(full);
    }

    public string? ReadText(string path)
    {
        var full = Resolve(path);
        return full is null ? null : File.ReadAllText(full);
    }

    DateTime LastWrite()
    {
        if (!Directory.Exists(root)) return DateTime.MinValue;
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        var newest = DateTime.MinValue;
        foreach (var file in files)
        {
            var written = File.GetLastWriteTimeUtc(file);
            if (written > newest) newest = written;
        }
        return newest;
    }

    /// Защита от выхода за каталог темы: текстом ("../../etc/passwd") или через симлинк на чужой файл.
    string? Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        var rooted = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rooted, StringComparison.Ordinal) || !File.Exists(full)) return null;

        var target = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
        if (target is not null && !target.StartsWith(rooted, StringComparison.Ordinal)) return null;

        return full;
    }
}
