using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdown.ColorCode;

namespace Samizdat.Core.Rendering;

public sealed class ArticleRenderer
{
    readonly MarkdownPipeline pipeline = SharedPipeline.Builder().UseColorCode().Build();

    /// slug нужен, чтобы собрать путь к вложениям статьи; attachmentBase его подменяет
    /// (гостевая страница отдаёт вложения через свой маршрут).
    /// diagrams: блок mermaid идёт схемой для app/assets/diagrams.js, иначе обычным кодом.
    public string Render(string markdown, string slug, IArticleLookup articles, string? attachmentBase = null,
                         bool diagrams = false)
    {
        var attachments = attachmentBase ?? $"/{slug}/";

        var document = Markdig.Markdown.Parse(markdown, pipeline);
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
}
