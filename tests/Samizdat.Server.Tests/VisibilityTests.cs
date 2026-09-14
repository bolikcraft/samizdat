using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
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
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
        var answer = await client.GetAsync("/tayna");

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task Reader_opens_a_shared_article()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
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
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
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
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
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
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
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
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var html = await client.GetStringAsync("/tayna");

        Assert.Contains("href=\"/tayna\"", html);
    }

    [Fact]
    public async Task Owner_opens_an_article_to_the_readers()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Private);
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var answer = await Post(client, "/visibility", new() { ["slug"] = "tayna", ["visibility"] = "shared" });

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
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
        var answer = await Post(client, "/visibility", new() { ["slug"] = "otkrytaya", ["visibility"] = "private" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task Closing_an_article_takes_it_out_of_the_cache()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "tayna", ArticleVisibility.Shared);
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var reader = await Login(factory, "ivan", "parol");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/tayna")).StatusCode);

        var owner = await Login(factory, "hozyain", "parol");
        await Post(owner, "/visibility", new() { ["slug"] = "tayna", ["visibility"] = "private" });

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/tayna")).StatusCode);
    }

    [Fact]
    public async Task Owner_and_reader_do_not_push_each_other_out_of_the_cache()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        await AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);
        await AddArticle(factory, "tayna", ArticleVisibility.Private);
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var owner = await Login(factory, "hozyain", "parol");
        var reader = await Login(factory, "ivan", "parol");

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

    static void AddPerson(WebApplicationFactory<Program> factory, string login, string password, UserRole role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login, PasswordHash = PasswordHasher.Hash(password), Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/"));
        fields[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    // Без автоперехода: иначе клиент сам сходит по редиректу и тест не увидит его кода.
    static async Task<HttpClient> Login(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var answer = await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return client;
    }
}
