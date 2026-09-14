using System.Net;
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
