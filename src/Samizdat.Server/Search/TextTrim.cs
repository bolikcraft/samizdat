namespace Samizdat.Server.Search;

/// Обрезка строки до предела длины, не разрывая суррогатную пару на границе.
public static class TextTrim
{
    /// Половина суррогатной пары на конце — уже не текст: драйвер не переводит её в UTF-8
    /// и валит выкладку.
    public static string Cut(string text, int limit)
    {
        if (limit <= 0) return "";
        if (text.Length <= limit) return text;

        var end = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..end];
    }
}
