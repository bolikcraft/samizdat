using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Samizdat.Core.Rendering;

/// Создаётся на каждый рендер: знает slug текущей статьи и список статей на сервере.
public sealed class WikiLinkRenderer(string currentSlug, IArticleLookup articles)
    : HtmlObjectRenderer<WikiLink>
{
    protected override void Write(HtmlRenderer renderer, WikiLink link)
    {
        if (link.IsEmbed)
        {
            renderer.Write("<img src=\"/").WriteEscapeUrl($"{currentSlug}/{link.Target}")
                    .Write("\" alt=\"").WriteEscape(link.Text).Write("\">");
            return;
        }

        if (!articles.Exists(link.Target))
        {
            renderer.WriteEscape(link.Text);
            return;
        }

        renderer.Write("<a href=\"/").WriteEscapeUrl(link.Target).Write("\">")
                .WriteEscape(link.Text).Write("</a>");
    }
}
