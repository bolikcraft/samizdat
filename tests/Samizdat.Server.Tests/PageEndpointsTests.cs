using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class PageEndpointsTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public PageEndpointsTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
        });
    }

    HttpClient StartServer() => LoginClient(StartFactory());

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

    // data/themes/default — каталог темы на диске, который переопределяет встроенную (см. Program.cs).
    void WriteThemeFile(string relativePath, string text)
    {
        var path = Path.Combine(dataRoot, "themes", "default", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    void WriteArticle(string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);
    }

    void Register(WebApplicationFactory<Program> factory, string slug, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    // Bearer-клиент на тот же factory: нужен, чтобы прогнать PUT /api/articles рядом с cookie-чтением страницы.
    HttpClient StartApiClient(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var owner = new UserRow
        {
            Login = $"owner-{Guid.NewGuid():N}",
            PasswordHash = PasswordHasher.Hash("тайна"),
            Role = UserRole.Owner,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(owner);
        db.SaveChanges();

        var token = ApiToken.Create();
        db.ApiTokens.Add(new ApiTokenRow
        {
            UserId = owner.Id,
            TokenHash = ApiToken.HashOf(token),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    static MultipartFormDataContent Article(string markdown)
        => new() { { new ByteArrayContent(Encoding.UTF8.GetBytes(markdown)), "index.md", "index.md" } };

    [Fact]
    public async Task Shows_article_page()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\n# Привет\n\nтекст\n");
        var factory = StartFactory();
        Register(factory, "privet", "Привет");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/privet");

        Assert.Contains("<h1>Привет</h1>", html);
        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_slug_returns_404_page()
    {
        var client = StartServer();

        var response = await client.GetAsync("/нет-такой");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Нет такой страницы", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Article_file_without_database_row_is_not_shown()
    {
        WriteArticle("сирота", "---\ntitle: Сирота\n---\nтекст\n");
        var client = StartServer();

        var response = await client.GetAsync("/сирота");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Serves_attachment_from_article_folder()
    {
        WriteArticle("s", "---\ntitle: T\n---\n![[pic.png]]");
        File.WriteAllBytes(Path.Combine(dataRoot, "articles", "s", "pic.png"), [1, 2, 3]);
        var factory = StartFactory();
        Register(factory, "s", "T");
        var client = LoginClient(factory);

        var response = await client.GetAsync("/s/pic.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Path_outside_article_folder_is_refused()
    {
        var client = StartServer();

        var response = await client.GetAsync("/s/..%2f..%2fappsettings.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Symlinked_attachment_pointing_outside_article_folder_is_refused()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"samizdat-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "чужие данные");
        try
        {
            WriteArticle("s", "---\ntitle: T\n---\nтекст\n");
            File.CreateSymbolicLink(Path.Combine(dataRoot, "articles", "s", "leak.txt"), secret);
            var factory = StartFactory();
            Register(factory, "s", "T");
            var client = LoginClient(factory);

            var response = await client.GetAsync("/s/leak.txt");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public async Task Article_title_with_markup_is_escaped()
    {
        WriteArticle("evil", "---\ntitle: \"</title><script>alert(1)</script>\"\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "evil", "</title><script>alert(1)</script>");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/evil");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Article_row_unchanged_serves_cached_page_even_if_file_on_disk_changed()
    {
        WriteArticle("s", "---\ntitle: Old\n---\nold text \n");
        var path = Path.Combine(dataRoot, "articles", "s", "index.md");
        var writeTime = File.GetLastWriteTimeUtc(path);
        var factory = StartFactory();
        Register(factory, "s", "Old");
        var client = LoginClient(factory);

        var before = await client.GetStringAsync("/s");
        Assert.Contains("Old", before);

        // Edit bypasses PUT, so the row's content_hash (the cache key) never changes.
        File.WriteAllText(path, "---\ntitle: New\n---\nnew text \n");
        File.SetLastWriteTimeUtc(path, writeTime);

        var after = await client.GetStringAsync("/s");

        Assert.Equal(before, after);
        Assert.Contains("Old", after);
    }

    [Fact]
    public async Task Put_with_unchanged_file_fingerprint_still_serves_new_content()
    {
        var factory = StartFactory();
        var api = StartApiClient(factory);
        var pagesClient = LoginClient(factory);

        await api.PutAsync("/api/articles/s", Article("---\ntitle: T\n---\nversion one\n"));
        var path = Path.Combine(dataRoot, "articles", "s", "index.md");
        var writeTime = File.GetLastWriteTimeUtc(path);

        var first = await pagesClient.GetStringAsync("/s");
        Assert.Contains("version one", first);

        await api.PutAsync("/api/articles/s", Article("---\ntitle: T\n---\nversion two\n"));
        // "version two" is the same byte length as "version one" — force the mtime to collide too,
        // imitating coarse filesystem time resolution after Replace's Directory.Move.
        File.SetLastWriteTimeUtc(path, writeTime);

        var second = await pagesClient.GetStringAsync("/s");

        Assert.Contains("version two", second);
        Assert.DoesNotContain("version one", second);
    }

    [Fact]
    public async Task Editing_theme_file_invalidates_cached_page()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\nтекст\n");
        WriteThemeFile("article.html", "old<h1>{{ article.title }}</h1>{{ article.html }}");
        var factory = StartFactory();
        Register(factory, "privet", "Привет");
        var client = LoginClient(factory);

        var before = await client.GetStringAsync("/privet");
        Assert.Contains("old", before);

        // File.GetLastWriteTimeUtc has 1-tick granularity on some filesystems; sleep to force a new value.
        await Task.Delay(20);
        WriteThemeFile("article.html", "new<h1>{{ article.title }}</h1>{{ article.html }}");
        var after = await client.GetStringAsync("/privet");

        Assert.Contains("new", after);
        Assert.DoesNotContain("old", after);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
