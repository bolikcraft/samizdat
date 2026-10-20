using System.IO.Compression;

namespace Samizdat.Server.Storage;

/// Папка статьи одним архивом. Внутри архива всё лежит в каталоге со slug в имени: распакованный
/// архив не должен рассыпаться по папке загрузок.
public sealed class ArticlePackage(string dataRoot)
{
    // Не /tmp: в контейнере это tmpfs, то есть память.
    readonly string folder = Path.Combine(dataRoot, "tmp");

    /// Архив во временном файле, поток стоит на его начале. Файл удаляется, когда поток закрывают.
    public FileStream Pack(string slug, byte[] markdown, IEnumerable<(string Name, string FullPath)> attachments)
    {
        Directory.CreateDirectory(folder);
        var file = new FileStream(Path.Combine(folder, $"{Guid.NewGuid():N}.zip"),
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Options = FileOptions.DeleteOnClose,
            });
        try
        {
            // leaveOpen: архив дописывает оглавление в Dispose, а файл нужен дальше — его отдают.
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                Write(zip, $"{slug}/index.md", markdown);
                foreach (var (name, path) in attachments)
                {
                    // Потоком, а не File.ReadAllBytes: большое вложение не ложится в память целиком.
                    using var source = File.OpenRead(path);
                    using var entry = zip.CreateEntry($"{slug}/{name}").Open();
                    source.CopyTo(entry);
                }
            }

            file.Position = 0;
            return file;
        }
        catch
        {
            // Вложение могли увести выкладкой посреди сборки: закрытие удаляет недописанный файл.
            file.Dispose();
            throw;
        }
    }

    /// Архивы, которые не удалил упавший процесс: DeleteOnClose срабатывает только при закрытии потока.
    public void RemoveLeftovers()
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
    }

    static void Write(ZipArchive zip, string path, byte[] bytes)
    {
        using var entry = zip.CreateEntry(path).Open();
        entry.Write(bytes);
    }
}
