using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Samizdat.Core.Rendering;

/// Текст статьи без разметки — то, что ищет полнотекстовый поиск.
public static class PlainText
{
    /// Управляющими символами размечается подсветка цитаты (см. SearchSnippet), в тексте статьи им
    /// не место. Перевод строки и табуляция законны и остаются.
    public static string WithoutControls(string text)
        => text.Any(Forbidden) ? new string(text.Where(symbol => !Forbidden(symbol)).ToArray()) : text;

    static bool Forbidden(char symbol) => symbol < ' ' && symbol is not ('\n' or '\r' or '\t');

    public static string Extract(string markdown)
        => Extract(Markdig.Markdown.Parse(markdown, IndexPipeline.Instance));

    // Документ мутируется на месте: CalloutTransformer.Apply меняет дерево (QuoteBlock →
    // CalloutBlock), так что зовите эту перегрузку после всего, что ждёт документ нетронутым
    // (например WikiLinks.Targets).
    public static string Extract(MarkdownDocument document)
    {
        // Без CalloutTransformer цитата "[!warning]" осталась бы AlertBlock — его встроенный
        // рендерер пишет сырой html (svg-иконку) в обход EnableHtmlForBlock; "[!note] Заголовок"
        // без трансформации ушёл бы в индекс маркером как есть, буквально со скобками.
        CalloutTransformer.Apply(document);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer)
        {
            EnableHtmlForBlock = false, EnableHtmlForInline = false, EnableHtmlEscape = false,
        };
        IndexPipeline.Instance.Setup(renderer);
        renderer.ObjectRenderers.Insert(0, new WikiLinkPlainRenderer());
        renderer.ObjectRenderers.Insert(0, new CalloutPlainRenderer());
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }

    sealed class CalloutPlainRenderer : HtmlObjectRenderer<CalloutBlock>
    {
        protected override void Write(HtmlRenderer renderer, CalloutBlock callout)
        {
            // Заголовок пишем, только если его задали явно — иначе это просто вид коллаута (kind).
            if (!string.Equals(callout.Title, callout.Kind, StringComparison.OrdinalIgnoreCase))
                renderer.Write(callout.Title).Write(" ");
            renderer.WriteChildren(callout);
        }
    }

    sealed class WikiLinkPlainRenderer : HtmlObjectRenderer<WikiLink>
    {
        protected override void Write(HtmlRenderer renderer, WikiLink link)
        {
            if (link.IsPictureEmbed) return;
            renderer.Write(link.Text);
        }
    }
}
