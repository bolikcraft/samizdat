using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class WikiLinkTests
{
    readonly ArticleRenderer renderer = new();
    static readonly IArticleLookup Known = new FakeLookup("proxmox");

    [Fact]
    public void Existing_article_becomes_link()
    {
        var html = renderer.Render("см. [[proxmox]]", "s", Known);

        Assert.Contains("<a href=\"/proxmox\">proxmox</a>", html);
    }

    [Fact]
    public void Link_with_label_uses_label()
    {
        var html = renderer.Render("см. [[proxmox|мой сервер]]", "s", Known);

        Assert.Contains("<a href=\"/proxmox\">мой сервер</a>", html);
    }

    [Fact]
    public void Unknown_article_becomes_plain_text()
    {
        var html = renderer.Render("см. [[secret|тайна]]", "s", Known);

        Assert.DoesNotContain("<a", html);
        Assert.Contains("тайна", html);
        Assert.DoesNotContain("secret", html);
    }

    [Fact]
    public void Normal_markdown_links_still_work()
    {
        var html = renderer.Render("[текст](https://example.com)", "s", Known);

        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public void Single_brackets_are_not_wiki_links()
    {
        var html = renderer.Render("массив [0] и [1]", "s", Known);

        Assert.Contains("[0]", html);
    }

    [Fact]
    public void Guest_render_turns_every_wiki_link_into_text()
    {
        var html = renderer.Render("[[drugaya-statya]]", "statya", NoArticles.Instance);

        Assert.Contains("drugaya-statya", html);
        Assert.DoesNotContain("<a href=", html);
    }
}

public sealed class FakeLookup(params string[] slugs) : IArticleLookup
{
    public bool Exists(string slug) => slugs.Contains(slug);
}
