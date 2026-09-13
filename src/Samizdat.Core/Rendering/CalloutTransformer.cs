using System.Text;
using System.Text.RegularExpressions;
using Markdig.Extensions.Alerts;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Samizdat.Core.Rendering;

/// Превращает цитаты вида "> [!note] Заголовок" в CalloutBlock после разбора документа.
public static partial class CalloutTransformer
{
    [GeneratedRegex(@"^\[!(?<kind>[a-zA-Zа-яА-Я-]+)\]\s*(?<title>.*)$")]
    private static partial Regex Marker();

    public static void Apply(MarkdownDocument document)
    {
        foreach (var block in document.Descendants<QuoteBlock>().ToList())
            TryConvert(block);
    }

    static void TryConvert(QuoteBlock quote)
    {
        // Родитель уже мог замениться на CalloutBlock превращением снаружи — сама цитата
        // при этом не трогается, просто переезжает к новому родителю.
        if (quote.Parent is null) return;

        string kind;
        string title;

        if (quote is AlertBlock alert)
        {
            // Markdig сам разбирает "[!type]" без заголовка через встроенное расширение
            // Alerts (входит в UseAdvancedExtensions) и уже вырезал маркер из текста.
            kind = alert.Kind.ToString().ToLowerInvariant();
            title = kind;
        }
        else if (!TryStripMarker(quote, out kind, out title))
        {
            return;
        }

        quote.ReplaceBy(new CalloutBlock { Kind = kind, Title = title });
    }

    static bool TryStripMarker(QuoteBlock quote, out string kind, out string title)
    {
        kind = title = "";
        if (quote.FirstOrDefault() is not ParagraphBlock { Inline: { } inline } paragraph) return false;

        // Ссылочный парсер Markdig пытается собрать "[...]" в обычную ссылку, не находит
        // "(url)" и откатывается, оставляя маркер разбитым на несколько LiteralInline.
        var firstLine = new StringBuilder();
        var afterMarker = inline.FirstChild;
        while (afterMarker is LiteralInline literal)
        {
            firstLine.Append(literal.Content.ToString());
            afterMarker = afterMarker.NextSibling;
        }

        var match = Marker().Match(firstLine.ToString());
        if (!match.Success) return false;

        kind = match.Groups["kind"].Value.ToLowerInvariant();
        var titleText = match.Groups["title"].Value.Trim();
        title = titleText.Length == 0 ? kind : titleText;

        var node = inline.FirstChild;
        while (node is LiteralInline && node != afterMarker)
        {
            var next = node.NextSibling;
            node.Remove();
            node = next;
        }

        if (afterMarker is LineBreakInline lineBreak)
            lineBreak.Remove();
        else if (afterMarker is null)
            quote.Remove(paragraph); // строка с маркером была во всей цитате единственной

        return true;
    }
}
