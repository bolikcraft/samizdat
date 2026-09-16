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

    [Fact]
    public void Note_name_becomes_a_link_to_its_slug()
    {
        var html = renderer.Render("см. [[Заметка про Proxmox]]", "s", new FakeLookup("zametka-pro-proxmox"));

        Assert.Contains("<a href=\"/zametka-pro-proxmox\">Заметка про Proxmox</a>", html);
    }

    [Fact]
    public void Link_with_an_anchor_leads_to_the_article()
    {
        var html = renderer.Render("см. [[proxmox#Диски]]", "s", Known);

        Assert.Contains("<a href=\"/proxmox\">", html);
    }

    [Fact]
    public void Link_with_an_emoji_renders()
    {
        var html = renderer.Render("см. [[x \U0001F389]]", "s", new FakeLookup("x"));

        Assert.Contains("<a href=\"/x\">", html);
    }
}

public sealed class FakeLookup(params string[] slugs) : IArticleLookup
{
    public string? Resolve(string target)
        => WikiLinkTarget.Candidates(target).FirstOrDefault(slugs.Contains);
}
