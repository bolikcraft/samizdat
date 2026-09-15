using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class IndexTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    [Fact]
    public void Search_vector_sees_the_word_in_another_form()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow
        {
            Slug = "server", Title = "Мой сервер", ContentHash = "x",
            SearchText = "В доме стоит тихий сервер.", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var found = db.Database
            .SqlQueryRaw<bool>("""SELECT "SearchVector" @@ websearch_to_tsquery('russian', 'серверы') AS "Value" FROM articles""")
            .Single();

        Assert.True(found);
    }

    [Fact]
    public void Meta_vector_holds_the_title_but_not_the_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow
        {
            Slug = "tayna", Title = "Тайный сервер", ContentHash = "x",
            SearchText = "гипервизор внутри", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var byTitle = db.Database
            .SqlQueryRaw<bool>("""SELECT "MetaVector" @@ websearch_to_tsquery('russian', 'серверы') AS "Value" FROM articles""")
            .Single();
        var byText = db.Database
            .SqlQueryRaw<bool>("""SELECT "MetaVector" @@ websearch_to_tsquery('russian', 'гипервизор') AS "Value" FROM articles""")
            .Single();

        Assert.True(byTitle);
        Assert.False(byText);
    }

    [Fact]
    public void Links_of_a_deleted_article_go_with_it()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow { Slug = "odna", Title = "Одна", ContentHash = "x" });
        db.SaveChanges();
        db.ArticleLinks.Add(new ArticleLinkRow { FromSlug = "odna", ToSlug = "drugaya" });
        db.SaveChanges();

        db.Articles.Remove(db.Articles.Single(row => row.Slug == "odna"));
        db.SaveChanges();

        Assert.Empty(db.ArticleLinks.ToList());
    }

    [Fact]
    public void Link_to_an_article_that_is_not_published_yet_is_allowed()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow { Slug = "odna", Title = "Одна", ContentHash = "x" });
        db.SaveChanges();
        db.ArticleLinks.Add(new ArticleLinkRow { FromSlug = "odna", ToSlug = "eshche-net" });
        db.SaveChanges();

        Assert.Single(db.ArticleLinks.ToList());
    }
}
