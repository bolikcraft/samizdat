using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdown.ColorCode;

namespace Samizdat.Core.Rendering;

public sealed class ArticleRenderer
{
    readonly MarkdownPipeline pipeline = SharedPipeline.Builder().UseColorCode().Build();

    /// slug нужен, чтобы собрать путь к вложениям статьи; attachmentBase его подменяет
    /// (гостевая страница отдаёт вложения через свой маршрут).
    /// diagrams: блок mermaid идёт схемой для app/assets/diagrams.js, иначе обычным кодом.
    /// title — заголовок, который тема печатает в h1. Такой же заголовок первым блоком заметки не выводится.
    public string Render(string markdown, string slug, IArticleLookup articles, string? attachmentBase = null,
                         bool diagrams = false, string? title = null)
    {
        var attachments = attachmentBase ?? $"/{slug}/";

        var document = Markdig.Markdown.Parse(markdown, pipeline);
        if (title is not null && document.FirstOrDefault() is HeadingBlock { Level: 1 } heading
            && string.Equals(TextOf(heading).Trim(), title.Trim(), StringComparison.Ordinal))
            document.Remove(heading);
        CalloutTransformer.Apply(document);
        ImagePathTransformer.Apply(document, attachments);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        // Рендерер кода ищем до вставки своего: MermaidRenderer отдаёт ему все прочие блоки.
        var code = renderer.ObjectRenderers.First(item => item.Accept(renderer, typeof(FencedCodeBlock)));
        renderer.ObjectRenderers.Insert(0, new MermaidRenderer(diagrams, code));
        renderer.ObjectRenderers.Insert(0, new WikiLinkRenderer(slug, articles, attachments));
        renderer.ObjectRenderers.Insert(0, new CalloutRenderer());
        renderer.Render(document);
        writer.Flush();
        // Чистится весь html: Markdig режет строчный HTML на отдельные теги, по одному их не проверить.
        return HtmlCleaner.Clean(writer.ToString());
    }

    /// Текст заголовка без разметки, как его видит читатель.
    static string TextOf(HeadingBlock heading)
    {
        var text = new StringBuilder();
        foreach (var inline in heading.Inline?.Descendants() ?? [])
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case WikiLink link: text.Append(link.Text); break;
                case HtmlEntityInline entity: text.Append(entity.Transcoded.ToString()); break;
            }
        }
        return text.ToString();
    }
}
