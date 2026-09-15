namespace Samizdat.Server.Storage;

public sealed record BackgroundContent(Stream Content, string ContentType, DateTimeOffset LastWrite);

/// Фон один на весь сайт: файл лежит в data/background/, имя хранится в настройке theme.background.
/// Настройку правит эндпоинт, этот класс знает только про файлы.
public sealed class BackgroundFile(string dataRoot)
{
    /// Сколько байт хватает отличить JPEG/PNG/WebP по сигнатуре — столько и просит ExtensionOf.
    public const int HeadLength = 12;

    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
    };

    readonly string root = Path.Combine(dataRoot, "background");

    /// Имя файла на диске для расширения — оно же значение настройки theme.background при выборе
    /// своей картинки. Известно заранее, до записи: эндпоинт сверяет его с базой первым делом.
    public static string NameFor(string extension) => $"background{extension}";

    /// Тип берём по первым байтам: расширение и Content-Type присылает браузер, им верить нельзя.
    public static string? ExtensionOf(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
        if (head.Length >= 8 && head[..8].SequenceEqual(PngSignature)) return ".png";
        if (head.Length >= HeadLength && head[..4].SequenceEqual("RIFF"u8) && head[8..HeadLength].SequenceEqual("WEBP"u8))
            return ".webp";
        return null;
    }

    /// Файл всегда один: новый пишем во временный и подменяем File.Move, старые расширения
    /// удаляем только после подмены — читатель не видит ни пустого, ни недописанного файла.
    public string Save(Stream content, string extension)
    {
        if (!Types.ContainsKey(extension))
            throw new ArgumentException($"Не умеем показывать {extension}", nameof(extension));

        Directory.CreateDirectory(root);
        var name = NameFor(extension);
        var target = Path.Combine(root, name);
        // Суффикс делает имя уникальным: два одновременных сохранения не столкнутся на File.Create.
        var tmp = $"{target}.{Guid.NewGuid():N}.tmp";

        try
        {
            using var file = File.Create(tmp);
            content.CopyTo(file);
        }
        catch
        {
            // Сбой уборки временного файла не должен заслонить настоящую причину падения.
            try { File.Delete(tmp); } catch { }
            throw;
        }

        File.Move(tmp, target, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(root))
            if (old != target) File.Delete(old);

        return name;
    }

    public void Remove()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    /// Имя загруженной картинки или null. Файл в каталоге всегда один, но недописанные временные
    /// файлы соседа тоже лежат тут — их отсекает проверка расширения.
    public string? Current()
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root).Select(Path.GetFileName)
                .FirstOrDefault(name => name is not null && Types.ContainsKey(Path.GetExtension(name)))
            : null;

    public BackgroundContent? Open(string name)
        => PathOf(name) is { } path
            ? new BackgroundContent(File.OpenRead(path), Types[Path.GetExtension(name)],
                                    File.GetLastWriteTimeUtc(path))
            : null;

    /// Метка времени файла. Входит в адрес картинки и в ключ кэша страниц: после замены фона
    /// браузер и кэш видят новое значение.
    public string Version(string name)
        => PathOf(name) is { } path ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "";

    /// null, если имени нет, оно не наше или выводит за каталог: в настройку могли записать что угодно.
    string? PathOf(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name)) return null;
        if (!Types.ContainsKey(Path.GetExtension(name))) return null;

        var path = Path.Combine(root, name);
        return File.Exists(path) ? path : null;
    }
}
