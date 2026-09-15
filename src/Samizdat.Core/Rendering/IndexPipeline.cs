using Markdig;

namespace Samizdat.Core.Rendering;

/// Конвейер разбора статьи для индекса. Те же расширения, что у рендера, но без подсветки кода:
/// в индекс едет текст, а не разметка.
public static class IndexPipeline
{
    public static readonly MarkdownPipeline Instance = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Use<WikiLinkExtension>()
        .Build();
}
