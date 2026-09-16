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
        if (link.IsPictureEmbed)
        {
            var fileName = link.FileName;
            var (alt, width, height) = EmbedSize.Parse(link.Label);

            // Без подписи alt — имя файла без расширения, а не технический суффикс ".png".
            renderer.Write("<img src=\"").WriteEscapeUrl($"{this.attachmentBase}{fileName}")
                    .Write("\" alt=\"").WriteEscape(alt ?? Path.GetFileNameWithoutExtension(fileName)).Write('"');
            if (width is not null) renderer.Write(" width=\"").Write(width).Write('"');
            if (height is not null) renderer.Write(" height=\"").Write(height).Write('"');
            renderer.Write('>');
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
