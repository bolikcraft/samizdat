using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class BackgroundTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public BackgroundTests(DatabaseFixture database)
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

    /// Кладём картинку мимо формы: этот файл проверяет только отдачу.
    void PutBackground(WebApplicationFactory<Program> factory, byte[] bytes, string name)
    {
        var folder = Path.Combine(dataRoot, "background");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set("theme.background", name);
    }

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

    [Fact]
    public async Task Without_a_background_the_address_gives_not_found()
    {
        var client = StartFactory().CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/background")).StatusCode);
    }

    [Fact]
    public async Task A_guest_gets_the_picture()
    {
        var factory = StartFactory();
        PutBackground(factory, Jpeg(), "background.jpg");

        var response = await factory.CreateClient().GetAsync("/background");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Jpeg(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_response_carries_an_etag_and_a_last_modified_header()
    {
        var factory = StartFactory();
        PutBackground(factory, Jpeg(), "background.jpg");

        var response = await factory.CreateClient().GetAsync("/background");

        Assert.NotNull(response.Headers.ETag);
        Assert.NotNull(response.Content.Headers.LastModified);
    }

    [Fact]
    public async Task The_page_of_the_owner_links_to_the_background()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        PutBackground(factory, Jpeg(), "background.jpg");

        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var html = await client.GetStringAsync("/");

        Assert.Contains("/background?v=", html);
        // Комментарий про escape в layout.html должен быть комментарием Scriban ({{ # ... }}),
        // а не HTML — иначе Scriban выведет его в разметку как есть.
        Assert.DoesNotContain("Экранировать", html);
    }

    [Fact]
    public async Task Without_a_background_the_page_has_no_link_to_it()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");

        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var html = await client.GetStringAsync("/");

        Assert.DoesNotContain("/background", html);
    }

    /// Настройка указывает на файл, которого уже нет на диске. Тут отработает File.Exists внутри
    /// Open, а не catch маршрута — саму гонку Exists/OpenRead юнит-тестом не воспроизвести детерминированно,
    /// но catch (IOException) в маршруте остаётся защитой на этот случай.
    [Fact]
    public async Task A_file_removed_behind_the_servers_back_gives_not_found_not_a_crash()
    {
        var factory = StartFactory();
        PutBackground(factory, Jpeg(), "background.jpg");
        File.Delete(Path.Combine(dataRoot, "background", "background.jpg"));

        var response = await factory.CreateClient().GetAsync("/background");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    async Task<HttpClient> OwnerClient(WebApplicationFactory<Program> factory)
    {
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        return client;
    }

    static MultipartFormDataContent Upload(string tokenName, string tokenValue, byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent { { new StringContent(tokenValue), tokenName } };
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", fileName);
        return content;
    }

    [Fact]
    public async Task The_owner_uploads_a_background_and_sees_it_on_the_page()
    {
        var factory = StartFactory();
        var client = await OwnerClient(factory);
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/background", Upload(name, value, Png(), "wall.png"));

        Assert.Contains("ok=background", response.RequestMessage!.RequestUri!.ToString());
        Assert.Equal(Png(), await client.GetByteArrayAsync("/background"));
    }

    // Браузеру верить нельзя: расширение и Content-Type он ставит какие угодно.
    [Fact]
    public async Task A_file_that_is_not_a_picture_is_refused()
    {
        var factory = StartFactory();
        var client = await OwnerClient(factory);
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/background",
            Upload(name, value, "MZ not a picture at all"u8.ToArray(), "wall.jpg"));

        Assert.Contains("err=background_type", response.RequestMessage!.RequestUri!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/background")).StatusCode);
    }

    [Fact]
    public async Task A_file_over_eight_megabytes_is_refused()
    {
        var factory = StartFactory();
        var client = await OwnerClient(factory);
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        var big = new byte[8 * 1024 * 1024 + 1];
        Jpeg().CopyTo(big, 0);

        var response = await client.PostAsync("/settings/background", Upload(name, value, big, "wall.jpg"));

        Assert.Contains("err=background_too_big", response.RequestMessage!.RequestUri!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/background")).StatusCode);
    }

    [Fact]
    public async Task An_empty_form_is_refused()
    {
        var factory = StartFactory();
        var client = await OwnerClient(factory);
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/background",
            new MultipartFormDataContent { { new StringContent(value), name } });

        Assert.Contains("err=background_missing", response.RequestMessage!.RequestUri!.ToString());
    }

    [Fact]
    public async Task The_second_upload_replaces_the_first_one()
    {
        var factory = StartFactory();
        var client = await OwnerClient(factory);
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        await client.PostAsync("/settings/background", Upload(name, value, Jpeg(), "first.jpg"));

        (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        await client.PostAsync("/settings/background", Upload(name, value, Png(), "second.png"));

        Assert.Equal(Png(), await client.GetByteArrayAsync("/background"));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(dataRoot, "background")));
    }

    [Fact]
    public async Task Removing_the_background_clears_the_file_and_the_setting()
    {
        var factory = StartFactory();
        var client = await OwnerClient(factory);
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        await client.PostAsync("/settings/background", Upload(name, value, Jpeg(), "wall.jpg"));

        (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));
        var response = await client.PostAsync("/settings/background/remove", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.Contains("ok=background_removed", response.RequestMessage!.RequestUri!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/background")).StatusCode);
        Assert.DoesNotContain("/background", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_guest_cannot_upload_a_background()
    {
        var client = StartFactory().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/settings/background",
            new MultipartFormDataContent { { new ByteArrayContent(Jpeg()), "file", "wall.jpg" } });

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/background")).StatusCode);
    }
}
