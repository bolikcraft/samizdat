using System.Security.Cryptography;
using System.Text;

namespace Samizdat.Core;

public static class ArticleHash
{
    /// Хэш всей статьи: markdown, вложения, папка в вольте и имя файла заметки. По нему CLI решает,
    /// нужна ли выкладка.
    public static string Compute(byte[] markdown, IReadOnlyCollection<(string Name, byte[] Bytes)> attachments,
                                  string folder, string? name = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(markdown);
        hash.AppendData(Encoding.UTF8.GetBytes(folder));

        foreach (var (attachment, bytes) in attachments.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(attachment));
            hash.AppendData(bytes);
        }

        // Без имени хэш прежний: старый CLI и плагин имени не шлют и должны сходиться с сервером.
        // Нулевой байт отделяет имя от байтов последнего вложения.
        if (name is not null)
        {
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(name));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
