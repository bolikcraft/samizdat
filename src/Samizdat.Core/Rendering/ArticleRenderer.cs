using Markdig;
using Markdig.Renderers;
using Markdown.ColorCode;

namespace Samizdat.Core.Rendering;

public sealed class ArticleRenderer
{
    // Не UseAdvancedExtensions(): он тянет автогенерацию id для заголовков,
    // а нам нужны только таблицы, списки задач и подсветка кода.
    readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseColorCode()
        .Build();

    /// slug нужен, чтобы собрать путь к вложениям статьи.
    public string Render(string markdown, string slug, IArticleLookup articles)
    {
        var document = Markdig.Markdown.Parse(markdown, pipeline);

        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        pipeline.Setup(renderer);
        renderer.Render(document);
        writer.Flush();
        return writer.ToString();
    }
}
