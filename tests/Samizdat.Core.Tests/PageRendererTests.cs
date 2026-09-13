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

    [Fact]
    public void Renders_a_template_that_includes_another_one()
    {
        var theme = new FakeTheme(new()
        {
            ["layout.html"] = "{{ content }}",
            ["page.html"] = "before {{ include 'partial' value: text }} after",
            ["partial.html"] = "[{{ $value }}]",
        });
        var renderer = new PageRenderer(theme);

        var page = renderer.Render("page.html", new Dictionary<string, object?> { ["text"] = "X" });

        Assert.Equal("before [X] after", page);
    }

    [Fact]
    public void Missing_include_throws_with_its_name()
    {
        var theme = new FakeTheme(new()
        {
            ["layout.html"] = "{{ content }}",
            ["page.html"] = "{{ include 'missing' }}",
        });
        var renderer = new PageRenderer(theme);

        var error = Assert.Throws<ThemeException>(() => renderer.Render("page.html", new()));

        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void Recursive_include_renders_a_deep_chain_without_hanging()
    {
        var theme = new FakeTheme(new()
        {
            ["layout.html"] = "{{ content }}",
            ["chain.html"] = "{{ include 'node' item: root }}",
            ["node.html"] = "({{ $item.name }}{{ if $item.child }}{{ include 'node' item: $item.child }}{{ end }})",
        });
        var renderer = new PageRenderer(theme);

        var page = renderer.Render("chain.html", new Dictionary<string, object?> { ["root"] = Chain(50) });

        Assert.Equal(ExpectedChain(50), page);
    }

    [Fact]
    public void Recursive_include_fails_instead_of_looping_forever_past_the_depth_limit()
    {
        var theme = new FakeTheme(new()
        {
            ["layout.html"] = "{{ content }}",
            ["chain.html"] = "{{ include 'node' item: root }}",
            ["node.html"] = "({{ $item.name }}{{ if $item.child }}{{ include 'node' item: $item.child }}{{ end }})",
        });
        var renderer = new PageRenderer(theme);

        // Глубже предела рекурсии Scriban (по умолчанию 100): без предела зациклилось бы навсегда,
        // с ним — управляемая ошибка вместо зависания.
        Assert.Throws<ThemeException>(() => renderer.Render(
            "chain.html", new Dictionary<string, object?> { ["root"] = Chain(150) }));
    }

    static Dictionary<string, object?> Chain(int depth)
    {
        Dictionary<string, object?>? child = null;
        for (var i = depth - 1; i >= 0; i--)
            child = new Dictionary<string, object?> { ["name"] = $"n{i}", ["child"] = child };
        return child!;
    }

    static string ExpectedChain(int depth)
        => string.Concat(Enumerable.Range(0, depth).Select(i => $"(n{i}")) + new string(')', depth);
}
