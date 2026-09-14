using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
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

    WebApplicationFactory<Program> StartFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    HttpClient LoginClient(WebApplicationFactory<Program> factory)
    {
        var login = $"owner-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Users.Add(new UserRow
            {
                Login = login,
                PasswordHash = PasswordHasher.Hash("тайна"),
                Role = UserRole.Owner,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        var client = factory.CreateClient();
        client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = "тайна" })).Wait();
        return client;
    }

    [Fact]
    public async Task Migrations_create_all_tables()
    {
        using var scope = StartFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        Assert.Empty(await db.Articles.ToListAsync());
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.ApiTokens.ToListAsync());
    }

    [Fact]
    public async Task Index_page_lists_articles_from_database()
    {
        var factory = StartFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow
            {
                Slug = "privet", Title = "Привет", ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var html = await LoginClient(factory).GetStringAsync("/");

        Assert.Contains("/privet", html);
        Assert.Contains("Привет", html);
    }

    [Fact]
    public async Task Wiki_link_to_article_in_database_becomes_link()
    {
        var factory = StartFactory();
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

        var html = await LoginClient(factory).GetStringAsync("/s");

        Assert.Contains("<a href=\"/privet\">", html);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
