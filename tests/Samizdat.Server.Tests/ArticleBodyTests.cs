using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

/// Текст статьи на её странице и на гостевой странице: схемы Mermaid и повторный заголовок.
[Collection("db")]
public class ArticleBodyTests : IDisposable
{
    const string WithDiagram = "---\ntitle: Схема\n---\n```mermaid\nflowchart LR\n  A --> B\n```\n";

    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public ArticleBodyTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task The_article_page_gives_a_diagram_block()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "shema", WithDiagram);
        var client = await TestLogin.AsOwner(factory);

        var html = await client.GetStringAsync("/shema");

        Assert.Contains("<pre class=\"mermaid\">flowchart LR\n  A --&gt; B\n</pre>", html);
    }

    [Fact]
    public async Task With_diagrams_off_the_article_page_gives_a_code_block()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "shema", WithDiagram);
        SetSetting(factory, "articles.diagrams", "off");
        var client = await TestLogin.AsOwner(factory);

        var html = await client.GetStringAsync("/shema");

        Assert.DoesNotContain("class=\"mermaid\"", html);
        Assert.Contains("<pre><code class=\"language-mermaid\">flowchart LR", html);
    }

    [Fact]
    public async Task A_cached_article_page_follows_the_diagrams_switch()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "shema", WithDiagram);
        var client = await TestLogin.AsOwner(factory);
        Assert.Contains("<pre class=\"mermaid\">", await client.GetStringAsync("/shema"));

        SetSetting(factory, "articles.diagrams", "off");

        Assert.Contains("<pre><code class=\"language-mermaid\">", await client.GetStringAsync("/shema"));
    }

    [Fact]
    public async Task The_guest_page_gives_a_diagram_block()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "shema", WithDiagram);
        var token = AddLink(factory, "shema");

        var html = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.Contains("<pre class=\"mermaid\">flowchart LR\n  A --&gt; B\n</pre>", html);
    }

    [Fact]
    public async Task With_diagrams_off_the_guest_page_gives_a_code_block()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "shema", WithDiagram);
        SetSetting(factory, "articles.diagrams", "off");
        var token = AddLink(factory, "shema");

        var html = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.DoesNotContain("class=\"mermaid\"", html);
        Assert.Contains("<pre><code class=\"language-mermaid\">flowchart LR", html);
    }

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    void AddArticle(WebApplicationFactory<Program> factory, string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = slug, ContentHash = $"hash-{slug}", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    static string AddLink(WebApplicationFactory<Program> factory, string slug)
    {
        var token = ShareToken.Create();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.ShareLinks.Add(new ShareLinkRow { Token = token, Slug = slug, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        return token;
    }

    static void SetSetting(WebApplicationFactory<Program> factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set(key, value);
    }
}
