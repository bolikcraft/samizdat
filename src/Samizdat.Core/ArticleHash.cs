using System.Security.Cryptography;
using System.Text;

namespace Samizdat.Core;

public static class ArticleHash
{
    /// Хэш всей статьи: markdown и вложения. По нему CLI решает, нужна ли выкладка.
    public static string Compute(byte[] markdown, IReadOnlyCollection<(string Name, byte[] Bytes)> attachments)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(markdown);

        foreach (var (name, bytes) in attachments.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
