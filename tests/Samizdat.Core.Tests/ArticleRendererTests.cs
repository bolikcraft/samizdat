using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class ArticleRendererTests
{
    readonly ArticleRenderer renderer = new();

    [Fact]
    public void Renders_headings_and_paragraphs()
    {
        var html = renderer.Render("# Заголовок\n\nтекст", "s", NoArticles.Instance);

        Assert.Contains("<h1", html);
        Assert.Contains("Заголовок</h1>", html);
    }

    [Fact]
    public void Renders_footnotes()
    {
        var html = renderer.Render("текст[^1]\n\n[^1]: примечание", "s", NoArticles.Instance);

        Assert.Contains("href=\"#fn:1\"", html);
    }

    [Fact]
    public void Renders_bare_urls_as_links()
    {
        var html = renderer.Render("см. https://example.com", "s", NoArticles.Instance);

        Assert.Contains("<a href=\"https://example.com\"", html);
    }

    [Fact]
    public void Renders_tables()
        => Assert.Contains("<table>", renderer.Render("| a | b |\n|---|---|\n| 1 | 2 |", "s", NoArticles.Instance));

    [Fact]
    public void Renders_task_lists()
        => Assert.Contains("type=\"checkbox\"", renderer.Render("- [x] готово", "s", NoArticles.Instance));

    [Fact]
    public void Highlights_code_blocks()
    {
        var html = renderer.Render("```csharp\nvar x = 1;\n```", "s", NoArticles.Instance);

        Assert.Contains("<span", html);
    }
}

public sealed class NoArticles : IArticleLookup
{
    public static readonly NoArticles Instance = new();
    public bool Exists(string slug) => false;
}
