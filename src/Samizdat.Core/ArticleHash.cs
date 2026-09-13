using System.Security.Cryptography;
using System.Text;

namespace Samizdat.Core;

public static class ArticleHash
{
    /// Хэш всей статьи: markdown, вложения и папка в вольте. По нему CLI решает, нужна ли выкладка.
    // folder по умолчанию "" — вызовы из CLI (Task 2) продолжают работать без правки.
    public static string Compute(byte[] markdown, IReadOnlyCollection<(string Name, byte[] Bytes)> attachments,
                                  string folder = "")
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(markdown);
        hash.AppendData(Encoding.UTF8.GetBytes(folder));

        foreach (var (name, bytes) in attachments.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
