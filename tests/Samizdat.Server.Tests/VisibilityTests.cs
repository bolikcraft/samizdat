using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class VisibilityTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public VisibilityTests(DatabaseFixture database) => this.database = database;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task Reader_does_not_open_a_private_article()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Private);

        var client = await TestLogin.AsReader(factory);
        var answer = await client.GetAsync("/tayna");

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task Reader_opens_a_shared_article()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);

        var client = await TestLogin.AsReader(factory);
        var answer = await client.GetAsync("/otkrytaya");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("Тайна", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Owner_opens_a_private_article()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Private);

        var client = await TestLogin.AsOwner(factory);
        var answer = await client.GetAsync("/tayna");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }

    [Fact]
    public async Task Picture_of_a_private_article_is_closed_for_the_reader()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Private);
        await File.WriteAllBytesAsync(Path.Combine(dataRoot, "articles", "tayna", "ezh.png"), [1, 2, 3]);

        var client = await TestLogin.AsReader(factory);
        var answer = await client.GetAsync("/tayna/ezh.png");

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task Reader_sees_the_title_of_a_private_article_but_not_a_link()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Private);
        await AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);

        var client = await TestLogin.AsReader(factory);
        var html = await client.GetStringAsync("/otkrytaya");

        Assert.Contains("nav-closed", html);
        Assert.DoesNotContain("href=\"/tayna\"", html);
        Assert.Contains("href=\"/otkrytaya\"", html);
    }

    [Fact]
    public async Task Owner_sees_every_article_as_a_link()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Private);

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/tayna");

        Assert.Contains("href=\"/tayna\"", html);
    }

    [Fact]
    public async Task Owner_opens_an_article_to_the_readers()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Private);

        var client = await TestLogin.AsOwner(factory);
        var answer = await TestLogin.Post(client, "/visibility", new() { ["slug"] = "tayna", ["visibility"] = "shared" });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/tayna", answer.Headers.Location?.ToString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single();
        Assert.Equal(ArticleVisibility.Shared, row.Visibility);
        Assert.NotNull(row.VisibilityChangedAt);
    }

    [Fact]
    public async Task Reader_does_not_switch_visibility()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);

        var client = await TestLogin.AsReader(factory);
        var answer = await TestLogin.Post(client, "/visibility", new() { ["slug"] = "otkrytaya", ["visibility"] = "private" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task Closing_an_article_takes_it_out_of_the_cache()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Shared);

        var reader = await TestLogin.AsReader(factory);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/tayna")).StatusCode);

        var owner = await TestLogin.AsOwner(factory);
        await TestLogin.Post(owner, "/visibility", new() { ["slug"] = "tayna", ["visibility"] = "private" });

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/tayna")).StatusCode);
    }

    [Fact]
    public async Task Owner_and_reader_do_not_push_each_other_out_of_the_cache()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);
        await AddArticle(factory, "tayna", ArticleVisibility.Private);

        var owner = await TestLogin.AsOwner(factory);
        var reader = await TestLogin.AsReader(factory);

        var ownerPage = await owner.GetStringAsync("/otkrytaya");
        var readerPage = await reader.GetStringAsync("/otkrytaya");
        var ownerAgain = await owner.GetStringAsync("/otkrytaya");

        Assert.Contains("/visibility", ownerPage);
        Assert.DoesNotContain("/visibility", readerPage);
        Assert.Contains("/visibility", ownerAgain);

        // Дыра, ради которой заведена ячейка reader/{slug}: из общей ячейки читателю пришла бы
        // страница владельца со ссылкой на закрытую статью.
        Assert.Contains("href=\"/tayna\"", ownerPage);
        Assert.DoesNotContain("href=\"/tayna\"", readerPage);
        Assert.Contains("href=\"/tayna\"", ownerAgain);
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

    async Task AddArticle(WebApplicationFactory<Program> factory, string slug, ArticleVisibility visibility)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.md"), "---\ntitle: Тайна\n---\n\nТекст.\n");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = "Тайна", ContentHash = $"hash-{slug}", Visibility = visibility,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
