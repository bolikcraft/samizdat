using System.IO.Compression;

namespace Samizdat.Server.Storage;

/// Папка статьи одним архивом. Внутри архива всё лежит в каталоге со slug в имени: распакованный
/// архив не должен рассыпаться по папке загрузок.
public static class ArticlePackage
{
    public static byte[] Pack(string slug, byte[] markdown, IEnumerable<(string Name, string FullPath)> attachments)
    {
        using var buffer = new MemoryStream();

        // leaveOpen: архив закрывается раньше буфера — он дописывает оглавление в Dispose,
        // и без этого ToArray вернул бы обрезанный файл.
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, $"{slug}/index.md", markdown);
            foreach (var (name, path) in attachments) Write(zip, $"{slug}/{name}", File.ReadAllBytes(path));
        }

        return buffer.ToArray();
    }

    static void Write(ZipArchive zip, string path, byte[] bytes)
    {
        using var entry = zip.CreateEntry(path).Open();
        entry.Write(bytes);
    }
}
