using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Search;

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
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
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
        Assert.Contains("This page does not exist", await response.Content.ReadAsStringAsync());
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
        WriteArticle("st", "---\ntitle: T\n---\n![[pic.png]]");
        File.WriteAllBytes(Path.Combine(dataRoot, "articles", "st", "pic.png"), [1, 2, 3]);
        var factory = StartFactory();
        Register(factory, "st", "T");
        var client = LoginClient(factory);

        var response = await client.GetAsync("/st/pic.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Path_outside_article_folder_is_refused()
    {
        // Путь с ".." сервер сводит к обычному ещё до маршрутизации, поэтому сюда файл не утечёт
        // даже без проверки границ каталога. Саму границу проверяет
        // ArticleFilesTests.Attachment_outside_the_article_folder_is_not_served.
        WriteArticle("st", "---\ntitle: T\n---\nтекст\n");
        File.WriteAllText(Path.Combine(dataRoot, "articles", "tayna.txt"), "секрет");
        var client = StartServer();

        var response = await client.GetAsync("/st/..%2ftayna.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Symlinked_attachment_pointing_outside_article_folder_is_refused()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"samizdat-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "чужие данные");
        try
        {
            WriteArticle("st", "---\ntitle: T\n---\nтекст\n");
            File.CreateSymbolicLink(Path.Combine(dataRoot, "articles", "st", "leak.txt"), secret);
            var factory = StartFactory();
            Register(factory, "st", "T");
            var client = LoginClient(factory);

            var response = await client.GetAsync("/st/leak.txt");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public async Task Control_character_from_the_front_matter_does_not_reach_the_page()
    {
        var factory = StartFactory();
        var api = TestPublisher.ClientWithToken(factory);
        // Заголовок и описание страница берёт заново из файла, в обход чистки перед выкладкой.
        var matter = $"title: Тайный{SearchSnippet.Start}сервер\ndescription: про{SearchSnippet.Stop}дом";
        (await TestPublisher.Push(api, "statya", $"---\n{matter}\n---\n\nтекст")).EnsureSuccessStatusCode();

        var client = LoginClient(factory);
        var html = await client.GetStringAsync("/statya");

        Assert.DoesNotContain(SearchSnippet.Start.ToString(), html, StringComparison.Ordinal);
        Assert.DoesNotContain(SearchSnippet.Stop.ToString(), html, StringComparison.Ordinal);
        Assert.Contains("Тайныйсервер", html);
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
        WriteArticle("st", "---\ntitle: Old\n---\nold text \n");
        var path = Path.Combine(dataRoot, "articles", "st", "index.md");
        var writeTime = File.GetLastWriteTimeUtc(path);
        var factory = StartFactory();
        Register(factory, "st", "Old");
        var client = LoginClient(factory);

        var before = await client.GetStringAsync("/st");
        Assert.Contains("Old", before);

        // Edit bypasses PUT, so the row's content_hash (the cache key) never changes.
        File.WriteAllText(path, "---\ntitle: New\n---\nnew text \n");
        File.SetLastWriteTimeUtc(path, writeTime);

        var after = await client.GetStringAsync("/st");

        // Cached html is byte-for-byte reused, but each response still gets a fresh real antiforgery
        // token substituted in (see PageEndpoints) — strip it out before comparing the rest.
        Assert.Equal(StripAntiforgeryToken(before), StripAntiforgeryToken(after));
        Assert.Contains("Old", after);
    }

    static readonly Regex AntiforgeryInput = new("""<input type="hidden" name="[^"]+" value="[^"]+">""");

    static string StripAntiforgeryToken(string html) => AntiforgeryInput.Replace(html, "");

    [Fact]
    public async Task Put_with_unchanged_file_fingerprint_still_serves_new_content()
    {
        var factory = StartFactory();
        var api = StartApiClient(factory);
        var pagesClient = LoginClient(factory);

        await api.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nversion one\n"));
        var path = Path.Combine(dataRoot, "articles", "st", "index.md");
        var writeTime = File.GetLastWriteTimeUtc(path);

        var first = await pagesClient.GetStringAsync("/st");
        Assert.Contains("version one", first);

        await api.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nversion two\n"));
        // "version two" is the same byte length as "version one" — force the mtime to collide too,
        // imitating coarse filesystem time resolution after Replace's Directory.Move.
        File.SetLastWriteTimeUtc(path, writeTime);

        var second = await pagesClient.GetStringAsync("/st");

        Assert.Contains("version two", second);
        Assert.DoesNotContain("version one", second);
    }

    [Fact]
    public async Task Editing_theme_file_invalidates_cached_page()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\nтекст\n");
        // Маркеры не должны быть словами из разметки макета (например, "placeholder" содержит "old").
        WriteThemeFile("article.html", "markerOld<h1>{{ article.title }}</h1>{{ article.html }}");
        var factory = StartFactory();
        Register(factory, "privet", "Привет");
        var client = LoginClient(factory);

        var before = await client.GetStringAsync("/privet");
        Assert.Contains("markerOld", before);

        // File.GetLastWriteTimeUtc has 1-tick granularity on some filesystems; sleep to force a new value.
        await Task.Delay(20);
        WriteThemeFile("article.html", "markerNew<h1>{{ article.title }}</h1>{{ article.html }}");
        var after = await client.GetStringAsync("/privet");

        Assert.Contains("markerNew", after);
        Assert.DoesNotContain("markerOld", after);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
