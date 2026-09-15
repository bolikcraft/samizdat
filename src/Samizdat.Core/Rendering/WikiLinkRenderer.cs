using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Samizdat.Core.Rendering;

/// Создаётся на каждый рендер: знает slug текущей статьи и список статей на сервере.
/// attachmentBase — префикс вложений; по умолчанию каталог самой статьи.
public sealed class WikiLinkRenderer(string currentSlug, IArticleLookup articles, string? attachmentBase = null)
    : HtmlObjectRenderer<WikiLink>
{
    readonly string attachmentBase = attachmentBase ?? $"/{currentSlug}/";

    protected override void Write(HtmlRenderer renderer, WikiLink link)
    {
        // Attachments live flat in the article folder: drop any directory part of the target
        // so an embed can't escape it (e.g. "../../secret.png").
        var fileName = Path.GetFileName(link.Target);

        if (link.IsPictureEmbed)
        {
            // Alt falls back to the file name without extension, not the technical ".png" suffix.
            var alt = link.Label ?? Path.GetFileNameWithoutExtension(fileName);
            renderer.Write("<img src=\"").WriteEscapeUrl($"{this.attachmentBase}{fileName}")
                    .Write("\" alt=\"").WriteEscape(alt).Write("\">");
            return;
        }

        if (articles.Resolve(link.Target) is not { } slug)
        {
            renderer.WriteEscape(link.Text);
            return;
        }

        renderer.Write("<a href=\"/").WriteEscapeUrl(slug).Write("\">")
                .WriteEscape(link.Text).Write("</a>");
    }
}
