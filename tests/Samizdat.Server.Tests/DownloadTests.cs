using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Tests;

public class ArticlePackageTests
{
    [Fact]
    public void The_archive_holds_one_folder_with_the_source_and_the_attachments()
    {
        var root = Directory.CreateTempSubdirectory("samizdat-zip").FullName;
        try
        {
            var picture = Path.Combine(root, "ezh.png");
            File.WriteAllBytes(picture, [1, 2, 3]);

            var bytes = ArticlePackage.Pack("tayna", Encoding.UTF8.GetBytes("текст"),
                                            [("ezh.png", picture)]);

            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            Assert.Equal(["tayna/index.md", "tayna/ezh.png"], zip.Entries.Select(entry => entry.FullName));

            using var source = new StreamReader(zip.GetEntry("tayna/index.md")!.Open());
            Assert.Equal("текст", source.ReadToEnd());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

[Collection("db")]
public class DownloadTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public DownloadTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task Owner_downloads_a_private_article_with_the_switches_off()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var answer = await client.GetAsync("/download/tayna");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("text/markdown", answer.Content.Headers.ContentType?.MediaType);
        Assert.Contains("tayna.md", answer.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task Owner_gets_the_file_byte_for_byte()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private,
                   "---\ntitle: Тайна\ntags: [заметки]\n---\n\nТекст.\n");
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var text = await client.GetStringAsync("/download/tayna");

        Assert.Equal("---\ntitle: Тайна\ntags: [заметки]\n---\n\nТекст.\n", text);
    }

    [Fact]
    public async Task Reader_gets_nothing_while_the_switch_is_off()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/download/otkrytaya")).StatusCode);
    }

    [Fact]
    public async Task Reader_gets_the_article_without_the_foreign_fields()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", ArticleVisibility.Shared,
                   "---\ntitle: Тайна\ntags: [заметки]\npublish: true\n---\n\nТекст.\n");
        AddPerson(factory, "ivan", "parol", UserRole.Reader);
        SetSetting(factory, "articles.download.readers", "on");

        var client = await Login(factory, "ivan", "parol");
        var text = await client.GetStringAsync("/download/otkrytaya");

        Assert.Contains("title: Тайна", text);
        Assert.DoesNotContain("tags", text);
        Assert.DoesNotContain("publish", text);
        Assert.Contains("Текст.", text);
    }

    [Fact]
    public async Task Reader_does_not_download_a_private_article()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);
        SetSetting(factory, "articles.download.readers", "on");

        var client = await Login(factory, "ivan", "parol");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/download/tayna")).StatusCode);
    }

    [Fact]
    public async Task A_guest_without_a_cookie_is_sent_to_the_login_page()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);
        SetSetting(factory, "articles.download.readers", "on");

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var answer = await client.GetAsync("/download/otkrytaya");

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        // CookieAuthenticationHandler строит абсолютный Location (схема+хост), поэтому сравниваем путь.
        Assert.StartsWith("/login", answer.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task An_unknown_article_is_not_found()
    {
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/download/net-takoy")).StatusCode);
    }

    [Fact]
    public async Task An_article_with_attachments_comes_as_a_zip()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        await File.WriteAllBytesAsync(Path.Combine(dataRoot, "articles", "tayna", "ezh.png"), [1, 2, 3]);
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var answer = await client.GetAsync("/download/tayna");

        Assert.Equal("application/zip", answer.Content.Headers.ContentType?.MediaType);
        Assert.Contains("tayna.zip", answer.Content.Headers.ContentDisposition?.ToString());

        using var zip = new ZipArchive(await answer.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Equal(["tayna/index.md", "tayna/ezh.png"], zip.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public async Task The_source_inside_the_archive_is_trimmed_for_the_reader()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", ArticleVisibility.Shared,
                   "---\ntitle: Тайна\ntags: [заметки]\n---\n\nТекст.\n");
        await File.WriteAllBytesAsync(Path.Combine(dataRoot, "articles", "otkrytaya", "ezh.png"), [1, 2, 3]);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);
        SetSetting(factory, "articles.download.readers", "on");

        var client = await Login(factory, "ivan", "parol");
        var answer = await client.GetAsync("/download/otkrytaya");

        using var zip = new ZipArchive(await answer.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        using var source = new StreamReader(zip.GetEntry("otkrytaya/index.md")!.Open());
        var text = await source.ReadToEndAsync();

        Assert.Contains("title: Тайна", text);
        Assert.DoesNotContain("tags", text);
    }

    [Fact]
    public async Task A_broken_header_is_not_handed_to_the_reader()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", ArticleVisibility.Shared, "---\ntitle: [не закрыт\n---\n\nТекст.\n");
        AddPerson(factory, "ivan", "parol", UserRole.Reader);
        SetSetting(factory, "articles.download.readers", "on");

        var client = await Login(factory, "ivan", "parol");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/download/otkrytaya")).StatusCode);
    }

    [Fact]
    public async Task A_guest_downloads_by_a_link_when_the_switch_is_on()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private,
                   "---\ntitle: Тайна\ntags: [заметки]\n---\n\nТекст.\n");
        SetSetting(factory, "articles.download.guests", "on");
        var token = AddLink(factory, "tayna");

        var client = factory.CreateClient();
        var answer = await client.GetAsync($"/download/s/{token}");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("no-store", answer.Headers.CacheControl?.ToString());

        var text = await answer.Content.ReadAsStringAsync();
        Assert.Contains("title: Тайна", text);
        Assert.DoesNotContain("tags", text);
    }

    [Fact]
    public async Task A_guest_gets_nothing_while_the_guest_switch_is_off()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        // Переключатель читателей гостя не касается.
        SetSetting(factory, "articles.download.readers", "on");
        var token = AddLink(factory, "tayna");

        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/download/s/{token}")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_link_downloads_nothing()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        SetSetting(factory, "articles.download.guests", "on");
        var token = AddLink(factory, "tayna", revoked: true);

        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/download/s/{token}")).StatusCode);
    }

    [Fact]
    public async Task Downloading_does_not_count_as_opening_the_link()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        SetSetting(factory, "articles.download.guests", "on");
        var token = AddLink(factory, "tayna");

        var client = factory.CreateClient();
        await client.GetAsync($"/download/s/{token}");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Equal(0, db.ShareLinks.Single().OpenedCount);
    }

    static string AddLink(WebApplicationFactory<Program> factory, string slug, bool revoked = false)
    {
        var token = ShareToken.Create();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.ShareLinks.Add(new ShareLinkRow
        {
            Token = token,
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
            RevokedAt = revoked ? DateTimeOffset.UtcNow : null,
        });
        db.SaveChanges();
        return token;
    }

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    void AddArticle(WebApplicationFactory<Program> factory, string slug, ArticleVisibility visibility,
                    string text = "---\ntitle: Тайна\n---\n\nТекст.\n")
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = "Тайна", ContentHash = $"hash-{slug}", Visibility = visibility,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
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

    static void SetSetting(WebApplicationFactory<Program> factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set(key, value);
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

    [Fact]
    public async Task Owner_sees_the_button_on_the_article()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var html = await client.GetStringAsync("/tayna");

        Assert.Contains("href=\"/download/tayna\"", html);
        Assert.Contains("Скачать .md", html);
    }

    [Fact]
    public async Task The_button_says_zip_when_the_article_has_attachments()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        await File.WriteAllBytesAsync(Path.Combine(dataRoot, "articles", "tayna", "ezh.png"), [1, 2, 3]);
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var html = await client.GetStringAsync("/tayna");

        Assert.Contains("Скачать .zip", html);
    }

    [Fact]
    public async Task Reader_sees_no_button_while_the_switch_is_off()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
        var html = await client.GetStringAsync("/otkrytaya");

        Assert.DoesNotContain("/download/otkrytaya", html);
    }

    [Fact]
    public async Task The_button_appears_on_a_page_that_already_sat_in_the_cache()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", ArticleVisibility.Shared);
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
        Assert.DoesNotContain("/download/otkrytaya", await client.GetStringAsync("/otkrytaya"));

        SetSetting(factory, "articles.download.readers", "on");

        Assert.Contains("href=\"/download/otkrytaya\"", await client.GetStringAsync("/otkrytaya"));
    }

    [Fact]
    public async Task A_guest_page_carries_the_address_of_its_own_link()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        SetSetting(factory, "articles.download.guests", "on");
        var first = AddLink(factory, "tayna");

        var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/s/{first}");

        Assert.Contains($"href=\"/download/s/{first}\"", html);
    }

    [Fact]
    public async Task Two_links_to_one_article_do_not_share_a_download_address()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        SetSetting(factory, "articles.download.guests", "on");
        var first = AddLink(factory, "tayna");
        var second = AddLink(factory, "tayna");

        var client = factory.CreateClient();
        // Первая страница кладёт статью в кэш — вторая обязана получить свой адрес, а не её.
        await client.GetStringAsync($"/s/{first}");
        var html = await client.GetStringAsync($"/s/{second}");

        Assert.Contains($"href=\"/download/s/{second}\"", html);
        Assert.DoesNotContain(first, html);
    }

    [Fact]
    public async Task A_guest_sees_no_button_while_the_guest_switch_is_off()
    {
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", ArticleVisibility.Private);
        var token = AddLink(factory, "tayna");

        var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/s/{token}");

        Assert.DoesNotContain("/download/s/", html);
    }

    [Fact]
    public async Task Owner_switches_downloading_on_from_the_settings_page()
    {
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var answer = await Post(client, "/settings/articles",
                                new() { ["readers"] = "on", ["guests"] = "on" });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?ok=articles#articles", answer.Headers.Location?.ToString());

        using var scope = factory.Services.CreateScope();
        Assert.Equal(new DownloadPolicy(true, true),
                     scope.ServiceProvider.GetRequiredService<SiteSettings>().Download);
    }

    [Fact]
    public async Task An_unchecked_box_switches_downloading_off()
    {
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        SetSetting(factory, "articles.download.readers", "on");
        SetSetting(factory, "articles.download.guests", "on");

        var client = await Login(factory, "hozyain", "parol");
        // Снятый флажок форма не присылает вовсе — приходит пустая форма.
        await Post(client, "/settings/articles", new());

        using var scope = factory.Services.CreateScope();
        Assert.Equal(new DownloadPolicy(false, false),
                     scope.ServiceProvider.GetRequiredService<SiteSettings>().Download);
    }

    [Fact]
    public async Task Reader_does_not_switch_downloading()
    {
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
        var answer = await Post(client, "/settings/articles", new() { ["readers"] = "on" });

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
    }

    [Fact]
    public async Task The_settings_page_shows_the_state_of_the_switches()
    {
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        SetSetting(factory, "articles.download.readers", "on");

        var client = await Login(factory, "hozyain", "parol");
        var html = await client.GetStringAsync("/settings");

        Assert.Contains("id=\"articles\"", html);
        Assert.Contains("name=\"readers\" value=\"on\" checked", html);
        Assert.Contains("name=\"guests\" value=\"on\">", html);
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        fields[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }
}
