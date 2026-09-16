using Samizdat.Core;

namespace Samizdat.Server.Storage;

public sealed class ArticleFiles(string dataRoot)
{
    public string ArticlesRoot { get; } = Path.Combine(dataRoot, "articles");

    public string Folder(string slug) => Path.Combine(ArticlesRoot, slug);

    /// Годится ли slug как имя каталога: без разделителей и управляющих символов, не с точки.
    /// Точка отделяет служебные .tmp- и .old- каталоги. Длину здесь не проверяем: метод стоит и на
    /// чтении, а статья с длинным slug могла лечь раньше, чем появился предел.
    public static bool IsValidSlug(string slug)
        => SafeName.IsSegment(slug) && !slug.StartsWith('.');

    // Первый сегмент адресов сайта: статья с таким именем была бы недоступна за своим адресом.
    // Имя занимают и наперёд, до появления самого маршрута. Регистр не важен — маршруты его
    // не различают.
    static readonly HashSet<string> ReservedSlugs =
        new(["s", "i", "login", "register", "settings", "assets", "background", "visibility",
             "share", "download", "search"],
            StringComparer.OrdinalIgnoreCase);

    public static bool IsReservedSlug(string slug) => ReservedSlugs.Contains(slug);

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

    /// Байты файла как есть — для владельца, который качает копию своей заметки. ReadAllText съел бы
    /// BOM, и «байт в байт» перестало бы быть правдой.
    public byte[]? ReadMarkdownBytes(string slug)
    {
        if (!IsValidSlug(slug)) return null;
        var file = Path.Combine(Folder(slug), "index.md");
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    /// Вложения статьи по алфавиту: имя и полный путь. Каждое имя проходит через AttachmentPath,
    /// поэтому index.md и имена с «\» сюда не попадают, а симлинк наружу отсеивается.
    public IEnumerable<(string Name, string FullPath)> Attachments(string slug)
    {
        var folder = Folder(slug);
        if (!IsValidSlug(slug) || !Directory.Exists(folder)) yield break;

        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (AttachmentPath(slug, name) is { } full) yield return (name, full);
        }
    }

    /// null, если имя выводит за каталог статьи — текстом или через симлинк на чужой файл.
    public string? AttachmentPath(string slug, string name)
    {
        if (!IsValidSlug(slug)) return null;
        // Вложения лежат в папке статьи плоско. Имя с каталогом отвергаем целиком: Path.GetFullPath
        // не разворачивает симлинк каталога, и "d/tayna.txt" увёл бы за пределы папки.
        // index.md отдают /api и /download своими путями с пересборкой шапки, а не этот метод:
        // как вложение он ушёл бы как есть, и чужой фронтматтер утёк бы читателю.
        if (!SafeName.IsAttachment(name)) return null;

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
    /// Старая версия остаётся в служебной папке до Commit/Rollback — запись саму по себе можно отменить.
    public ArticleWrite BeginReplace(string slug, byte[] markdown,
                                      IReadOnlyCollection<(string Name, byte[] Bytes)> attachments)
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
                // PUT уже отвечает 400 на такое имя. Бросаем внутри try: staging-каталог уберёт catch.
                if (!SafeName.IsAttachment(name))
                    throw new ArgumentException($"Недопустимое имя вложения «{name}»", nameof(attachments));
                File.WriteAllBytes(Path.Combine(staging, name), bytes);
            }
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }

        var old = Path.Combine(ArticlesRoot, $".old-{slug}-{Guid.NewGuid():N}");
        var hadOldVersion = Directory.Exists(target);
        if (hadOldVersion) Directory.Move(target, old);
        Directory.Move(staging, target);

        return new ArticleWrite(target, old, hadOldVersion);
    }

    /// Запись без возможности отмены — там, где сохранение в базу не может отказать после неё.
    public void Replace(string slug, byte[] markdown, IReadOnlyCollection<(string Name, byte[] Bytes)> attachments)
        => BeginReplace(slug, markdown, attachments).Commit();

    public void Remove(string slug)
    {
        if (!IsValidSlug(slug)) return;
        var folder = Folder(slug);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}

/// Запись статьи на диск, которую ещё можно отменить: база может отказать уже после того,
/// как файлы легли на место.
public sealed class ArticleWrite(string target, string old, bool hadOldVersion)
{
    bool done;

    /// Публикация подтверждена — старая версия больше не нужна.
    public void Commit()
    {
        if (done) return;
        done = true;
        if (hadOldVersion) Directory.Delete(old, recursive: true);
    }

    /// База отказала — возвращаем прежнюю версию на место, а для новой статьи убираем только что созданную папку.
    public void Rollback()
    {
        if (done) return;
        done = true;
        Directory.Delete(target, recursive: true);
        if (hadOldVersion) Directory.Move(old, target);
    }
}
