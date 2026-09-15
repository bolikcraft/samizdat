using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Samizdat.Core.Rendering;

/// Текст статьи без разметки — то, что ищет полнотекстовый поиск.
public static class PlainText
{
    public static string Extract(string markdown)
    {
        // Коллауты не разворачиваем: без своего рендера блок пропал бы целиком вместе с текстом.
        // Обычной цитатой он отдаёт те же слова, лишним в индексе будет только тип "[!note]".
        var document = Markdig.Markdown.Parse(markdown, IndexPipeline.Instance);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer)
        {
            EnableHtmlForBlock = false, EnableHtmlForInline = false, EnableHtmlEscape = false,
        };
        IndexPipeline.Instance.Setup(renderer);
        renderer.ObjectRenderers.Insert(0, new WikiLinkPlainRenderer());
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
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
