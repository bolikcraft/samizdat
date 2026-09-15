using Markdig.Syntax;

namespace Samizdat.Core.Rendering;

/// Цели вики-ссылок статьи — из них собирается таблица обратных ссылок.
public static class WikiLinks
{
    public static IReadOnlyList<string> Targets(string markdown)
        => Markdig.Markdown.Parse(markdown, IndexPipeline.Instance)
            .Descendants<WikiLink>()
            .Where(link => !link.IsPictureEmbed)
            .Select(link => link.Target)
            .ToList();
}
