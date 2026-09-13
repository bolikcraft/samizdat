using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Samizdat.Core.Rendering;

/// Переписывает относительные адреса картинок обычной разметки на базу вложений.
/// Без этого браузер считает адрес от страницы статьи и файл не находится.
public static class ImagePathTransformer
{
    public static void Apply(MarkdownDocument document, string attachmentBase)
    {
        foreach (var image in document.Descendants<LinkInline>().ToList())
        {
            if (!image.IsImage) continue;
            if (image.Url is not { Length: > 0 } url) continue;
            if (url.StartsWith('/') || HasScheme(url)) continue;

            // Вложения лежат в папке статьи плоско, поэтому от адреса берётся только имя файла.
            image.Url = attachmentBase + Path.GetFileName(url);
        }
    }

    /// Схема по RFC 3986: буква, дальше буквы, цифры и "+-.", и всё это до двоеточия.
    static bool HasScheme(string url)
    {
        var colon = url.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(url[0])) return false;

        for (var i = 1; i < colon; i++)
            if (!char.IsAsciiLetterOrDigit(url[i]) && url[i] is not ('+' or '-' or '.'))
                return false;

        return true;
    }
}
