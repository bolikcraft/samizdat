using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class CalloutTests
{
    readonly ArticleRenderer renderer = new();

    string Render(string markdown) => renderer.Render(markdown, "s", NoArticles.Instance);

    [Fact]
    public void Note_callout_has_class_and_body()
    {
        var html = Render("> [!note]\n> текст\n");

        Assert.Contains("class=\"callout callout-note\"", html);
        Assert.Contains("текст", html);
        Assert.DoesNotContain("[!note]", html);
    }

    [Fact]
    public void Title_after_type_becomes_header()
    {
        var html = Render("> [!warning] Осторожно\n> текст\n");

        Assert.Contains("class=\"callout callout-warning\"", html);
        Assert.Contains("<div class=\"callout-title\">Осторожно</div>", html);
    }

    [Fact]
    public void Type_without_title_uses_type_name_as_header()
    {
        var html = Render("> [!tip]\n> текст\n");

        Assert.Contains("<div class=\"callout-title\">tip</div>", html);
    }

    [Fact]
    public void Unknown_type_still_renders_as_callout()
        => Assert.Contains("callout-cite", Render("> [!cite] Источник\n> текст\n"));

    [Fact]
    public void Plain_quote_stays_a_quote()
    {
        var html = Render("> обычная цитата\n");

        Assert.Contains("<blockquote>", html);
        Assert.DoesNotContain("callout", html);
    }

    [Fact]
    public void Markdown_inside_callout_is_rendered()
    {
        var html = Render("> [!note] Т\n> **жирный** и [[нет]]\n");

        Assert.Contains("<strong>жирный</strong>", html);
    }

    [Fact]
    public void Callout_nested_inside_callout_keeps_both_bodies()
    {
        var html = Render("> [!note] Снаружи\n> внешний текст\n>\n> > [!tip] Внутри\n> > внутренний текст\n");

        Assert.Contains("class=\"callout callout-note\"", html);
        Assert.Contains("class=\"callout callout-tip\"", html);
        Assert.Contains("внешний текст", html);
        Assert.Contains("внутренний текст", html);
        Assert.DoesNotContain("[!note]", html);
        Assert.DoesNotContain("[!tip]", html);
    }

    [Fact]
    public void Callout_nested_inside_plain_quote_keeps_both()
    {
        var html = Render("> обычная\n>\n> > [!note] Вложенный\n> > текст\n");

        Assert.Contains("<blockquote>", html);
        Assert.Contains("class=\"callout callout-note\"", html);
        Assert.Contains("обычная", html);
        Assert.Contains("текст", html);
    }
}
