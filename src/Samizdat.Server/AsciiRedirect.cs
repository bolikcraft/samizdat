using System.Text;

namespace Samizdat.Server;

public static class AsciiRedirect
{
    // Location — HTTP-заголовок, туда идёт только ASCII. Не-ASCII байты UTF-8 кодируем в %XX,
    // остальное (уже закодированное, знаки пути) не трогаем.
    public static string Target(string url)
    {
        if (url.All(char.IsAscii)) return url;
        var target = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(url))
            if (b < 0x80) target.Append((char)b);
            else target.Append('%').Append(b.ToString("X2"));
        return target.ToString();
    }
}
