using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

/// Проверяет, что Task 5 действительно заставляет кэш страниц замечать смену вида
/// (тему, схему, фон): без ViewFingerprint в ключе старый html из PageCache пережил бы
/// правку настроек. Хелперы — копия из BackgroundTests.cs, дублирование осознанное.
[Collection("db")]
public class PageLookCacheTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public PageLookCacheTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    WebApplicationFactory<Program> StartFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
        });

    static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

    void AddOwner(WebApplicationFactory<Program> factory, string login, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login,
            PasswordHash = PasswordHasher.Hash(password),
            Role = UserRole.Owner,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    async Task<HttpClient> OwnerClient(WebApplicationFactory<Program> factory)
    {
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        return client;
    }

    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    // Content-Type всегда image/jpeg: сервер судит по байтам, не по заголовку.
    static MultipartFormDataContent Upload(string tokenName, string tokenValue, byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent { { new StringContent(tokenValue), tokenName } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", fileName);
        return content;
    }

    async Task UploadBackground(HttpClient client)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        await client.PostAsync("/settings/background", Upload(name, value, Jpeg(), "wall.jpg"));
    }

    void WriteArticle(string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);
    }

    void RegisterArticle(WebApplicationFactory<Program> factory, string slug, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task An_article_page_picks_up_a_new_colour_scheme()
    {
        WriteArticle("zametka", "---\ntitle: Заметка\n---\nтекст\n");
        var factory = StartFactory();
        RegisterArticle(factory, "zametka", "Заметка");
        var client = await OwnerClient(factory);

        var before = await client.GetStringAsync("/zametka");
        Assert.Contains("data-color-scheme=\"system\"", before);

        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["theme"] = "default", ["color_scheme"] = "dark" }));

        var after = await client.GetStringAsync("/zametka");
        Assert.Contains("data-color-scheme=\"dark\"", after);
    }

    [Fact]
    public async Task An_article_page_picks_up_a_newly_uploaded_background()
    {
        WriteArticle("zametka", "---\ntitle: Заметка\n---\nтекст\n");
        var factory = StartFactory();
        RegisterArticle(factory, "zametka", "Заметка");
        var client = await OwnerClient(factory);

        var before = await client.GetStringAsync("/zametka");
        Assert.DoesNotContain("/background", before);

        await UploadBackground(client);

        var after = await client.GetStringAsync("/zametka");
        Assert.Contains("/background?v=", after);
    }

    [Fact]
    public async Task The_guest_page_behind_a_share_link_picks_up_a_newly_uploaded_background()
    {
        WriteArticle("zametka", "---\ntitle: Заметка\n---\nтекст\n");
        var factory = StartFactory();
        RegisterArticle(factory, "zametka", "Заметка");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.ShareLinks.Add(new ShareLinkRow { Token = "gost", Slug = "zametka", CreatedAt = DateTimeOffset.UtcNow });
            db.SaveChanges();
        }

        var guest = factory.CreateClient();
        var before = await guest.GetStringAsync("/s/gost");
        Assert.DoesNotContain("/background", before);

        var owner = await OwnerClient(factory);
        await UploadBackground(owner);

        var after = await guest.GetStringAsync("/s/gost");
        Assert.Contains("/background?v=", after);
    }
}
