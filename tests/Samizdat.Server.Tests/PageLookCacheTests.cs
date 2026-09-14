using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Rendering;

namespace Samizdat.Server.Tests;

/// Кэш страниц должен замечать смену вида (тему, схему, фон): без ViewFingerprint в ключе
/// старый html из PageCache пережил бы правку настроек.
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
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];

    // Ищем объявление свойства, а не один адрес: голый "/background?v=" нашёлся бы и в комментарии
    // темы, утёкшем в разметку, — ровно тот баг, что чинил 95df17b.
    const string BackgroundStyle = "--bg-image: url(\"/background?v=";

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

    void RegisterArticle(WebApplicationFactory<Program> factory, string slug, string title,
                         ArticleVisibility visibility = ArticleVisibility.Private)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = "x", Visibility = visibility,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    void AddShareLink(WebApplicationFactory<Program> factory, string token, string slug)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.ShareLinks.Add(new ShareLinkRow { Token = token, Slug = slug, CreatedAt = DateTimeOffset.UtcNow });
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
        Assert.DoesNotContain("--bg-image", before);

        await UploadBackground(client);

        var after = await client.GetStringAsync("/zametka");
        Assert.Contains(BackgroundStyle, after);
    }

    [Fact]
    public async Task An_article_page_loses_the_background_after_it_is_removed()
    {
        WriteArticle("zametka", "---\ntitle: Заметка\n---\nтекст\n");
        var factory = StartFactory();
        RegisterArticle(factory, "zametka", "Заметка");
        var client = await OwnerClient(factory);

        await UploadBackground(client);
        Assert.Contains(BackgroundStyle, await client.GetStringAsync("/zametka"));

        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        await client.PostAsync("/settings/background/remove", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.DoesNotContain("--bg-image", await client.GetStringAsync("/zametka"));
    }

    [Fact]
    public async Task The_guest_page_behind_a_share_link_picks_up_a_newly_uploaded_background()
    {
        WriteArticle("zametka", "---\ntitle: Заметка\n---\nтекст\n");
        var factory = StartFactory();
        RegisterArticle(factory, "zametka", "Заметка", ArticleVisibility.Shared);
        AddShareLink(factory, "gost", "zametka");

        var guest = factory.CreateClient();
        var before = await guest.GetStringAsync("/s/gost");
        Assert.DoesNotContain("--bg-image", before);

        var owner = await OwnerClient(factory);
        await UploadBackground(owner);

        var after = await guest.GetStringAsync("/s/gost");
        Assert.Contains(BackgroundStyle, after);
    }

    [Fact]
    public async Task The_guest_page_behind_a_share_link_picks_up_a_new_colour_scheme()
    {
        WriteArticle("zametka", "---\ntitle: Заметка\n---\nтекст\n");
        var factory = StartFactory();
        RegisterArticle(factory, "zametka", "Заметка", ArticleVisibility.Shared);
        AddShareLink(factory, "gost", "zametka");

        var guest = factory.CreateClient();
        Assert.Contains("data-color-scheme=\"system\"", await guest.GetStringAsync("/s/gost"));

        var owner = await OwnerClient(factory);
        var (name, value) = AntiforgeryToken(await owner.GetStringAsync("/settings"));
        await owner.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["theme"] = "default", ["color_scheme"] = "dark" }));

        Assert.Contains("data-color-scheme=\"dark\"", await guest.GetStringAsync("/s/gost"));
    }

    /// ThemeFactory отдаёт для темы, которой нет на диске, встроенную default, поэтому у двух
    /// таких имён одинаковый theme.Version. В ключе кэша их различает только имя из отпечатка вида.
    [Fact]
    public void The_view_fingerprint_separates_two_theme_names_with_the_same_files()
    {
        var factory = StartFactory();
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();
        var themes = scope.ServiceProvider.GetRequiredService<ThemeFactory>();

        Assert.Equal(themes.Get("pervaya").Version, themes.Get("vtoraya").Version);

        settings.Set("theme.name", "pervaya");
        var first = settings.ViewFingerprint;
        settings.Set("theme.name", "vtoraya");

        Assert.NotEqual(first, settings.ViewFingerprint);
    }
}
