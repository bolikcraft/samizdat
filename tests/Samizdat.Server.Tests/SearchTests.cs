using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Data;
using Samizdat.Server.Search;

namespace Samizdat.Server.Tests;

public class SearchSnippetTests
{
    [Fact]
    public void Marks_become_tags()
        => Assert.Equal("тут <mark>сервер</mark> стоит",
                        SearchSnippet.ToHtml($"тут {SearchSnippet.Start}сервер{SearchSnippet.Stop} стоит"));

    [Fact]
    public void Html_from_the_text_is_escaped()
        => Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt;",
                        SearchSnippet.ToHtml("<script>alert(1)</script>"));

    [Fact]
    public void Escaping_goes_before_the_marks()
    {
        var html = SearchSnippet.ToHtml($"{SearchSnippet.Start}<b>{SearchSnippet.Stop}");

        Assert.Equal("<mark>&lt;b&gt;</mark>", html);
    }
}

[Collection("db")]
public class ArticleSearchTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    void AddArticle(WebApplicationFactory<Program> factory, string slug, string title, string text,
                    ArticleVisibility visibility)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = "h", IndexedHash = "h", SearchText = text,
            Visibility = visibility, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    async Task<IReadOnlyList<SearchHit>> Find(WebApplicationFactory<Program> factory, string query, bool isOwner)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ArticleSearch>().Find(query, isOwner);
    }

    [Fact]
    public async Task Morphology_finds_a_word_in_another_form()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "В доме стоит тихий сервер.", ArticleVisibility.Private);

        var hits = await Find(factory, "серверы", isOwner: true);

        Assert.Equal("dom", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Snippet_shows_the_found_word()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "В доме стоит тихий сервер.", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.Contains($"{SearchSnippet.Start}сервер{SearchSnippet.Stop}", hit.Snippet);
    }

    [Fact]
    public async Task Phrase_in_quotes_is_a_phrase()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "odna", "Одна", "тихий сервер в доме", ArticleVisibility.Private);
        AddArticle(factory, "drugaya", "Другая", "сервер шумит, дом тихий", ArticleVisibility.Private);

        var hits = await Find(factory, "\"тихий сервер\"", isOwner: true);

        Assert.Equal("odna", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Minus_word_takes_an_article_away()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "odna", "Одна", "сервер в доме", ArticleVisibility.Private);
        AddArticle(factory, "drugaya", "Другая", "сервер в офисе", ArticleVisibility.Private);

        var hits = await Find(factory, "сервер -офис", isOwner: true);

        Assert.Equal("odna", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Broken_query_gives_an_empty_list()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "текст", ArticleVisibility.Private);

        Assert.Empty(await Find(factory, "\"\"\" & | !", isOwner: true));
    }

    [Fact]
    public async Task Reader_does_not_find_a_private_article_by_its_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайна", "тут стоит сервер", ArticleVisibility.Private);

        Assert.Empty(await Find(factory, "сервер", isOwner: false));
    }

    [Fact]
    public async Task Reader_finds_a_private_article_by_its_title_without_a_snippet()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайный сервер", "секретный текст", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "серверы", isOwner: false));

        Assert.False(hit.CanOpen);
        Assert.Null(hit.Snippet);
    }

    [Fact]
    public async Task Reader_finds_a_shared_article_by_its_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", "Открытая", "тут стоит сервер", ArticleVisibility.Shared);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: false));

        Assert.True(hit.CanOpen);
        Assert.NotNull(hit.Snippet);
    }

    [Fact]
    public async Task Owner_finds_a_private_article_with_a_snippet()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайна", "тут стоит сервер", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.True(hit.CanOpen);
        Assert.NotNull(hit.Snippet);
    }

    [Fact]
    public async Task Title_weighs_more_than_the_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "v-tekste", "Про дом", "тут стоит сервер", ArticleVisibility.Private);
        AddArticle(factory, "v-zagolovke", "Мой сервер", "текст про дом", ArticleVisibility.Private);

        var hits = await Find(factory, "сервер", isOwner: true);

        Assert.Equal("v-zagolovke", hits[0].Slug);
    }

    [Fact]
    public async Task Trigrams_catch_a_typo()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "proxmox", "Proxmox", "гипервизор дома", ArticleVisibility.Private);

        using var scope = factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ArticleSearch>();

        Assert.Empty(await search.Find("proxmoks", isOwner: true));
        Assert.Equal("proxmox", Assert.Single(await search.FindSimilar("proxmoks", isOwner: true)).Slug);
    }

    [Fact]
    public async Task Trigrams_keep_the_text_of_a_private_article_closed()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайна", "гипервизор", ArticleVisibility.Private);

        using var scope = factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ArticleSearch>();

        Assert.Empty(await search.FindSimilar("гипервизер", isOwner: false));
    }
}
