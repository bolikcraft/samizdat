using Samizdat.Core.Themes;

namespace Samizdat.Core.Tests;

public class PageRendererTests
{
    sealed class FakeTheme(Dictionary<string, string> files) : IThemeSource
    {
        public string Version { get; set; } = "1";
        public Stream? OpenRead(string path) => null;
        public string? ReadText(string path) => files.GetValueOrDefault(path);
        public void Set(string path, string text) => files[path] = text;
    }

    [Fact]
    public void Puts_template_output_into_layout()
    {
        var theme = new FakeTheme(new()
        {
            ["layout.html"] = "<html><title>{{ page_title }}</title>{{ content }}</html>",
            ["article.html"] = "<h1>{{ article.title }}</h1>{{ article.html }}",
        });
        var renderer = new PageRenderer(theme);

        var page = renderer.Render("article.html", new Dictionary<string, object?>
        {
            ["page_title"] = "Привет",
            ["article"] = new Dictionary<string, object?> { ["title"] = "Привет", ["html"] = "<p>текст</p>" },
        });

        Assert.Equal("<html><title>Привет</title><h1>Привет</h1><p>текст</p></html>", page);
    }

    [Fact]
    public void Missing_template_throws_with_name()
    {
        var renderer = new PageRenderer(new FakeTheme(new()));

        var error = Assert.Throws<ThemeException>(() => renderer.Render("article.html", new()));

        Assert.Contains("article.html", error.Message);
    }

    [Fact]
    public void Missing_layout_throws_with_name()
    {
        var theme = new FakeTheme(new() { ["article.html"] = "ok" });
        var renderer = new PageRenderer(theme);

        var error = Assert.Throws<ThemeException>(() => renderer.Render("article.html", new()));

        Assert.Contains("layout.html", error.Message);
    }

    [Fact]
    public void Broken_template_throws_with_position()
    {
        var theme = new FakeTheme(new()
        {
            ["layout.html"] = "{{ content }}",
            ["article.html"] = "{{ if x }}",
        });
        var renderer = new PageRenderer(theme);

        Assert.Throws<ThemeException>(() => renderer.Render("article.html", new()));
    }

    [Fact]
    public void Rereads_template_when_theme_version_changes()
    {
        var theme = new FakeTheme(new()
        {
            ["layout.html"] = "{{ content }}",
            ["article.html"] = "old",
        });
        var renderer = new PageRenderer(theme);
        renderer.Render("article.html", new());

        theme.Version = "2";
        theme.Set("article.html", "new");

        var page = renderer.Render("article.html", new());

        Assert.Equal("new", page);
    }
}
