using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class ArticleRendererTests
{
    readonly ArticleRenderer renderer = new();

    [Fact]
    public void Renders_headings_and_paragraphs()
        => Assert.Contains("<h1>Заголовок</h1>", renderer.Render("# Заголовок\n\nтекст", "s", NoArticles.Instance));

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
