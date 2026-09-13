using Markdig;
using Markdig.Renderers;
using Markdown.ColorCode;

namespace Samizdat.Core.Rendering;

public sealed class ArticleRenderer
{
    readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseColorCode()
        .Use<WikiLinkExtension>()
        .Build();

    /// slug нужен, чтобы собрать путь к вложениям статьи.
    public string Render(string markdown, string slug, IArticleLookup articles)
    {
        var document = Markdig.Markdown.Parse(markdown, pipeline);
        CalloutTransformer.Apply(document);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.ObjectRenderers.Insert(0, new WikiLinkRenderer(slug, articles));
        renderer.ObjectRenderers.Insert(0, new CalloutRenderer());
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }
}
