namespace Samizdat.Core.Themes;

public sealed class DiskThemeSource(string root) : IThemeSource
{
    public string Version => LastWrite().ToString("O");

    public Stream? OpenRead(string path)
    {
        var full = Resolve(path);
        return full is null || !File.Exists(full) ? null : File.OpenRead(full);
    }

    public string? ReadText(string path)
    {
        var full = Resolve(path);
        return full is null || !File.Exists(full) ? null : File.ReadAllText(full);
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

    /// Защита от выхода за каталог темы: "../../etc/passwd" не проходит.
    string? Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path));
        var rooted = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        return full.StartsWith(rooted, StringComparison.Ordinal) ? full : null;
    }
}
