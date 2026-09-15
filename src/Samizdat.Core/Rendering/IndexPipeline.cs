using Markdig;

namespace Samizdat.Core.Rendering;

/// Конвейер разбора статьи для индекса.
public static class IndexPipeline
{
    public static readonly MarkdownPipeline Instance = SharedPipeline.Builder().Build();
}
