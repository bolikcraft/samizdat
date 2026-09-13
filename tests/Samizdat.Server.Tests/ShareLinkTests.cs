using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ShareLinkTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public ShareLinkTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
        });

    async Task<HttpClient> LoginClient(WebApplicationFactory<Program> factory)
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

        // Без автоперехода: тесты проверяют сам редирект после POST. Вход это не ломает —
        // cookie ставится уже на первом ответе.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = "тайна" }));
        return client;
    }

    void WriteArticle(string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);
    }

    void WriteAttachment(string slug, string name, byte[] bytes)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
    }

    void RegisterArticle(WebApplicationFactory<Program> factory, string slug, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    string AddLink(WebApplicationFactory<Program> factory, string slug,
                   DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var link = new ShareLinkRow
        {
            Token = ShareToken.Create(),
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
        };
        db.ShareLinks.Add(link);
        db.SaveChanges();
        return link.Token;
    }

    ShareLinkRow LinkByToken(WebApplicationFactory<Program> factory, string token)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        return db.ShareLinks.First(row => row.Token == token);
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public void Token_is_short_and_unique()
    {
        var first = ShareToken.Create();
        var second = ShareToken.Create();

        Assert.Equal(22, first.Length);
        Assert.NotEqual(first, second);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
    }

    [Fact]
    public void Deleting_an_article_deletes_its_links()
    {
        using var factory = StartFactory();
        RegisterArticle(factory, "statya", "Статья");
        var token = AddLink(factory, "statya");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Remove(db.Articles.Find("statya")!);
            db.SaveChanges();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            Assert.Empty(db.ShareLinks.Where(row => row.Token == token));
        }
    }

    [Fact]
    public async Task Live_link_shows_the_article_to_anonymous()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "---\ntitle: Про ежей\n---\n\nТекст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var token = AddLink(factory, "statya");

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/s/{token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Текст статьи.", html);
        Assert.Contains("Про ежей", html);
    }

    [Fact]
    public async Task Guest_page_has_no_navigation_and_no_owner_menu()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        RegisterArticle(factory, "chuzhaya", "Чужая статья");
        var token = AddLink(factory, "statya");

        var html = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.DoesNotContain("nav-tree", html);
        Assert.DoesNotContain("user-menu", html);
        Assert.DoesNotContain("chuzhaya", html);
        Assert.Contains("noindex", html);
    }

    [Fact]
    public async Task Expired_and_revoked_links_answer_410()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var expired = AddLink(factory, "statya", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var revoked = AddLink(factory, "statya", revokedAt: DateTimeOffset.UtcNow);

        var client = factory.CreateClient();
        var first = await client.GetAsync($"/s/{expired}");
        var second = await client.GetAsync($"/s/{revoked}");

        Assert.Equal(HttpStatusCode.Gone, first.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
        Assert.Contains("больше не работает", await first.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_token_answers_404()
    {
        using var factory = StartFactory();

        var response = await factory.CreateClient().GetAsync("/s/ZZZZZZZZZZZZZZZZZZZZZZ");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Opening_the_page_counts_the_visit()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var token = AddLink(factory, "statya");

        var client = factory.CreateClient();
        await client.GetAsync($"/s/{token}");
        await client.GetAsync($"/s/{token}");

        var link = LinkByToken(factory, token);
        Assert.Equal(2, link.OpenedCount);
        Assert.NotNull(link.LastOpenedAt);
    }
}
