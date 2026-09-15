using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class PublishApiTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    (WebApplicationFactory<Program> Factory, HttpClient Client) StartWithToken()
    {
        var (factory, client, _, _) = StartWithTokenAndOwner();
        return (factory, client);
    }

    (WebApplicationFactory<Program> Factory, HttpClient Client, string Login, string Password) StartWithTokenAndOwner()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

        var (client, login, password) = TestPublisher.ClientWithOwner(factory);
        return (factory, client, login, password);
    }

    // Страницы сайта открывает cookie-сессия, а не Bearer-токен: логинимся отдельным клиентом.
    static async Task<HttpClient> LoginPageClient(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
        return client;
    }

    static MultipartFormDataContent Article(string markdown, params (string Name, byte[] Bytes)[] files) =>
        TestPublisher.Form(markdown, files);

    [Fact]
    public async Task Put_writes_file_and_row()
    {
        var (factory, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/privet",
            Article("---\ntitle: Привет\n---\nтекст\n"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(dataRoot, "articles", "privet", "index.md")));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single(article => article.Slug == "privet");
        Assert.Equal("Привет", row.Title);
        Assert.NotEmpty(row.ContentHash);
    }

    // Статьи в этом файле делят строку по slug в общей базе (коллекция "db"): свой slug на тест,
    // иначе проверка "строки нет" ловит чужую строку, оставленную другим тестом.
    static string UniqueSlug() => $"folder-{Guid.NewGuid():N}";

    [Fact]
    public async Task Put_stores_the_folder_field()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        var content = Article("---\ntitle: Привет\n---\nтекст\n");
        content.Add(new StringContent("Заметки/PROXMOX"), "folder");

        var response = await client.PutAsync($"/api/articles/{slug}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Equal("Заметки/PROXMOX", db.Articles.Single(article => article.Slug == slug).Folder);
    }

    [Fact]
    public async Task Put_without_folder_field_stores_empty_folder()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();

        var response = await client.PutAsync($"/api/articles/{slug}", Article("текст"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Equal("", db.Articles.Single(article => article.Slug == slug).Folder);
    }

    [Theory]
    [InlineData("../секрет")]
    [InlineData("/корень")]
    [InlineData("Заметки//пусто")]
    [InlineData("Заметки\\x")]
    public async Task Put_with_bad_folder_is_refused_and_article_is_not_written(string folder)
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        var content = Article("текст");
        content.Add(new StringContent(folder), "folder");

        var response = await client.PutAsync($"/api/articles/{slug}", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", slug)));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.Articles.Where(article => article.Slug == slug));
    }

    [Fact]
    public async Task Put_replaces_previous_version_and_removes_old_attachments()
    {
        var (_, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("v1", ("old.png", [1])));

        await client.PutAsync("/api/articles/st", Article("v2", ("new.png", [2])));

        var folder = Path.Combine(dataRoot, "articles", "st");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(folder, "index.md")));
        Assert.True(File.Exists(Path.Combine(folder, "new.png")));
        Assert.False(File.Exists(Path.Combine(folder, "old.png")));
    }

    [Fact]
    public async Task State_returns_slug_to_hash()
    {
        var (_, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("текст"));

        var state = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/state");

        Assert.NotNull(state);
        Assert.True(state!.ContainsKey("st"));
    }

    [Fact]
    public async Task Delete_removes_file_and_row()
    {
        var (factory, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("текст"));

        var response = await client.DeleteAsync("/api/articles/st");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", "st")));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.Articles.Where(article => article.Slug == "st"));
    }

    [Fact]
    public async Task Get_markdown_returns_source()
    {
        var (_, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nтело\n"));

        var text = await client.GetStringAsync("/api/articles/st.md");

        Assert.Contains("тело", text);
    }

    [Fact]
    public async Task Broken_front_matter_returns_400_with_file_name()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/st", Article("---\ntitle: [\n---\nx"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("фронтматтер", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Slug_with_slash_is_refused()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/..%2fetc", Article("x"));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        Assert.False(File.Exists(Path.Combine(dataRoot, "..", "etc", "index.md")));
    }

    [Fact]
    public async Task Slug_starting_with_dot_is_refused()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/.hidden", Article("x"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", ".hidden")));
    }

    // %2f/%2F в значении маршрута ASP.NET Core намеренно не декодирует в "/" (иначе слаг мог бы
    // расползтись на сегменты пути) — слаг остаётся текстом с процентами и не расширяет каталог.
    [Theory]
    [InlineData("a%2fb")]
    [InlineData("a%2Fb")]
    public async Task Percent_encoded_slash_in_slug_stays_a_literal_file_name(string rawSlug)
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync($"/api/articles/{rawSlug}", Article("x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(dataRoot, "articles", rawSlug, "index.md")));
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", "a")));
    }

    [Fact]
    public async Task Empty_slug_is_refused()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/", Article("x"));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
                    or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Page_reflects_new_content_after_put()
    {
        var (factory, api, login, password) = StartWithTokenAndOwner();
        var pages = await LoginPageClient(factory, login, password);

        await api.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nверсия раз\n"));
        var first = await pages.GetStringAsync("/st");
        Assert.Contains("версия раз", first);

        await api.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nверсия два\n"));
        var second = await pages.GetStringAsync("/st");

        Assert.Contains("версия два", second);
        Assert.DoesNotContain("версия раз", second);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
