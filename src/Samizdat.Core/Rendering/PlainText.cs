using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Samizdat.Core.Rendering;

/// Текст статьи без разметки — то, что ищет полнотекстовый поиск.
public static class PlainText
{
    public static string Extract(string markdown)
    {
        // Без CalloutTransformer цитата "[!warning]" осталась бы AlertBlock — его встроенный
        // рендерер пишет сырой html (svg-иконку) в обход EnableHtmlForBlock; "[!note] Заголовок"
        // без трансформации ушёл бы в индекс маркером как есть, буквально со скобками.
        var document = Markdig.Markdown.Parse(markdown, IndexPipeline.Instance);
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
            if (link.IsEmbed && WikiLink.IsImage(link.Target)) return;
            renderer.Write(link.Text);
        }
    }
}
