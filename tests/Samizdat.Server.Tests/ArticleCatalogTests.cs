using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ArticleCatalogTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public ArticleCatalogTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
        });

    [Fact]
    public async Task Migrations_create_all_tables()
    {
        using var scope = StartServer().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        Assert.Empty(await db.Articles.ToListAsync());
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.ApiTokens.ToListAsync());
    }

    [Fact]
    public async Task Index_page_lists_articles_from_database()
    {
        var factory = StartServer();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow
            {
                Slug = "privet", Title = "Привет", ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var html = await factory.CreateClient().GetStringAsync("/");

        Assert.Contains("/privet", html);
        Assert.Contains("Привет", html);
    }

    [Fact]
    public async Task Wiki_link_to_article_in_database_becomes_link()
    {
        var factory = StartServer();
        Directory.CreateDirectory(Path.Combine(dataRoot, "articles", "s"));
        File.WriteAllText(Path.Combine(dataRoot, "articles", "s", "index.md"), "[[privet]]");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow
            {
                Slug = "privet", Title = "Привет", ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Articles.Add(new ArticleRow
            {
                Slug = "s", Title = "T", ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var html = await factory.CreateClient().GetStringAsync("/s");

        Assert.Contains("<a href=\"/privet\">", html);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
