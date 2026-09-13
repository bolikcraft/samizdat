namespace Samizdat.Server.Storage;

public sealed class ArticleFiles(string dataRoot)
{
    public string ArticlesRoot { get; } = Path.Combine(dataRoot, "articles");

    public string Folder(string slug) => Path.Combine(ArticlesRoot, slug);

    public string? ReadMarkdown(string slug)
    {
        var file = Path.Combine(Folder(slug), "index.md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// Отпечаток файла без чтения содержимого: время записи и длина. Ключ кэша страниц строится по нему.
    public string? Fingerprint(string slug)
    {
        var info = new FileInfo(Path.Combine(Folder(slug), "index.md"));
        return info.Exists ? $"{info.LastWriteTimeUtc:O}|{info.Length}" : null;
    }

    /// null, если имя выводит за каталог статьи — текстом или через симлинк на чужой файл.
    public string? AttachmentPath(string slug, string name)
    {
        var folder = Path.GetFullPath(Folder(slug)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, name));
        if (!full.StartsWith(folder, StringComparison.Ordinal) || !File.Exists(full)) return null;

        var target = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
        if (target is not null && !target.StartsWith(folder, StringComparison.Ordinal)) return null;

        return full;
    }

    public IEnumerable<string> AllSlugs()
        => Directory.Exists(ArticlesRoot)
            ? Directory.EnumerateDirectories(ArticlesRoot).Select(dir => Path.GetFileName(dir)!)
            : [];
}
