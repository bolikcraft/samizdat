using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class EmbedTests
{
    readonly ArticleRenderer renderer = new();

    [Fact]
    public void Image_embed_points_to_article_folder()
    {
        var html = renderer.Render("![[схема.png]]", "proxmox", NoArticles.Instance);

        Assert.Contains("<img src=\"/proxmox/", html);
        Assert.Contains(".png\"", html);
    }

    [Fact]
    public void Name_with_spaces_is_url_encoded()
    {
        var html = renderer.Render("![[моя схема.png]]", "s", NoArticles.Instance);

        Assert.Contains("%20", html);
        Assert.DoesNotContain("моя схема.png\"", html);
    }

    [Fact]
    public void Note_embed_without_extension_renders_as_link_text()
    {
        var html = renderer.Render("![[Другая заметка]]", "s", NoArticles.Instance);

        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public void Normal_markdown_image_still_works()
    {
        var html = renderer.Render("![подпись](/s/pic.png)", "s", NoArticles.Instance);

        Assert.Contains("<img src=\"/s/pic.png\"", html);
    }

    [Fact]
    public void Path_traversal_in_target_is_stripped_to_file_name()
    {
        var html = renderer.Render("![[../../secret.png]]", "s", NoArticles.Instance);

        Assert.Contains("<img src=\"/s/secret.png\"", html);
        Assert.DoesNotContain("..", html);
    }

    [Fact]
    public void Embed_uses_given_attachment_base()
    {
        var html = renderer.Render("![[shema.png]]", "statya", NoArticles.Instance,
                                   attachmentBase: "/s/abc/");

        Assert.Contains("""<img src="/s/abc/shema.png" alt="shema">""", html);
    }

    [Fact]
    public void Embed_without_attachment_base_points_to_article_folder()
    {
        var html = renderer.Render("![[shema.png]]", "statya", NoArticles.Instance);

        Assert.Contains("""<img src="/statya/shema.png" alt="shema">""", html);
    }
}
