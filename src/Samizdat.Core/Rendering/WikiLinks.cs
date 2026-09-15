using Markdig.Syntax;

namespace Samizdat.Core.Rendering;

/// Цели вики-ссылок статьи — из них собирается таблица обратных ссылок.
public static class WikiLinks
{
    public static IReadOnlyList<string> Targets(string markdown)
        => Targets(Markdig.Markdown.Parse(markdown, IndexPipeline.Instance));

    // Документ передавайте до CalloutTransformer.Apply — ссылки собираются так же, как при
    // разборе строки с нуля (тот тоже не трансформирует коллауты).
    public static IReadOnlyList<string> Targets(MarkdownDocument document)
        => document
            .Descendants<WikiLink>()
            .Where(link => !link.IsPictureEmbed)
            .Select(link => link.Target)
            .ToList();
}
