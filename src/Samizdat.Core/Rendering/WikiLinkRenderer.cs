using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Samizdat.Core.Rendering;

/// Создаётся на каждый рендер: знает slug текущей статьи и список статей на сервере.
public sealed class WikiLinkRenderer(string currentSlug, IArticleLookup articles)
    : HtmlObjectRenderer<WikiLink>
{
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".avif"];

    protected override void Write(HtmlRenderer renderer, WikiLink link)
    {
        // Attachments live flat in the article folder: drop any directory part of the target
        // so an embed can't escape it (e.g. "../../secret.png").
        var fileName = Path.GetFileName(link.Target);

        if (link.IsEmbed && HasImageExtension(fileName))
        {
            // Alt falls back to the file name without extension, not the technical ".png" suffix.
            var alt = link.Label ?? Path.GetFileNameWithoutExtension(fileName);
            renderer.Write("<img src=\"/").WriteEscapeUrl($"{currentSlug}/{fileName}")
                    .Write("\" alt=\"").WriteEscape(alt).Write("\">");
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

    static bool HasImageExtension(string name)
        => ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
}
