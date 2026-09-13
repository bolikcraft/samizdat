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

    /// null, если имя выводит за каталог статьи.
    public string? AttachmentPath(string slug, string name)
    {
        var folder = Path.GetFullPath(Folder(slug)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, name));
        return full.StartsWith(folder, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public IEnumerable<string> AllSlugs()
        => Directory.Exists(ArticlesRoot)
            ? Directory.EnumerateDirectories(ArticlesRoot).Select(dir => Path.GetFileName(dir)!)
            : [];
}
