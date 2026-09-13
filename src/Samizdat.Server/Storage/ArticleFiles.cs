namespace Samizdat.Server.Storage;

public sealed class ArticleFiles(string dataRoot)
{
    public string ArticlesRoot { get; } = Path.Combine(dataRoot, "articles");

    public string Folder(string slug) => Path.Combine(ArticlesRoot, slug);

    /// Без слэшей, без "..", не начинается с точки — точка отделяет служебные .tmp-/.old- каталоги.
    public static bool IsValidSlug(string slug)
        => slug.Length > 0 && slug == Path.GetFileName(slug) && !slug.StartsWith('.');

    /// Адрес /s/ отдан ссылкам для гостей: статья с таким slug была бы недоступна.
    /// Регистр не важен — маршруты его не различают, и "S" уехал бы в гостевой маршрут.
    public static bool IsReservedSlug(string slug) => slug.Equals("s", StringComparison.OrdinalIgnoreCase);

    /// Путь папки в вольте: сегменты через "/", без выхода вверх и без пустых сегментов.
    public static bool IsValidFolder(string folder)
        => folder.Length == 0
           || (!folder.StartsWith('/') && !folder.EndsWith('/') && !folder.Contains('\\')
               && folder.Split('/').All(part => part.Length > 0 && part != "." && part != ".."));

    public string? ReadMarkdown(string slug)
    {
        if (!IsValidSlug(slug)) return null;
        var file = Path.Combine(Folder(slug), "index.md");
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// Проверка без чтения содержимого — для отбраковки осиротевшей в БД строки на горячем пути кэша.
    public bool MarkdownExists(string slug)
        => IsValidSlug(slug) && File.Exists(Path.Combine(Folder(slug), "index.md"));

    /// null, если имя выводит за каталог статьи — текстом или через симлинк на чужой файл.
    public string? AttachmentPath(string slug, string name)
    {
        if (!IsValidSlug(slug)) return null;
        // Вложения лежат в папке статьи плоско. Имя с каталогом отвергаем целиком: Path.GetFullPath
        // не разворачивает симлинк каталога, и "d/tayna.txt" увёл бы за пределы папки.
        if (name != Path.GetFileName(name)) return null;
        // Исходник статьи отдаёт только /api: у гостя иначе утечёт фронтматтер.
        if (name.Equals("index.md", StringComparison.OrdinalIgnoreCase)) return null;

        var folder = Path.GetFullPath(Folder(slug)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, name));
        if (!full.StartsWith(folder, StringComparison.Ordinal) || !File.Exists(full)) return null;

        var target = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName;
        if (target is not null && !target.StartsWith(folder, StringComparison.Ordinal)) return null;

        return full;
    }

    public IEnumerable<string> AllSlugs()
        => Directory.Exists(ArticlesRoot)
            ? Directory.EnumerateDirectories(ArticlesRoot).Select(dir => Path.GetFileName(dir)!).Where(IsValidSlug)
            : [];

    /// Кладём новую версию рядом и переносим одним движением: читатель не видит половину статьи.
    public void Replace(string slug, byte[] markdown, IReadOnlyCollection<(string Name, byte[] Bytes)> attachments)
    {
        if (!IsValidSlug(slug)) throw new ArgumentException("Плохой slug", nameof(slug));

        var target = Folder(slug);
        var staging = Path.Combine(ArticlesRoot, $".tmp-{slug}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            File.WriteAllBytes(Path.Combine(staging, "index.md"), markdown);
            foreach (var (name, bytes) in attachments)
            {
                var safe = Path.GetFileName(name);
                if (safe.Length == 0 || safe is "." or "..") continue;
                File.WriteAllBytes(Path.Combine(staging, safe), bytes);
            }
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }

        var old = Path.Combine(ArticlesRoot, $".old-{slug}-{Guid.NewGuid():N}");
        if (Directory.Exists(target)) Directory.Move(target, old);
        Directory.Move(staging, target);
        if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
    }

    public void Remove(string slug)
    {
        if (!IsValidSlug(slug)) return;
        var folder = Folder(slug);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
