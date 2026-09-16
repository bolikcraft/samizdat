using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class AuthTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public AuthTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    void AddOwner(WebApplicationFactory<Program> factory, string login, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.RemoveRange(db.Users);
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
    public void Password_hash_is_verified_and_differs_every_time()
    {
        var first = PasswordHasher.Hash("тайна");
        var second = PasswordHasher.Hash("тайна");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("тайна", first));
        Assert.False(PasswordHasher.Verify("не та", first));
    }

    [Fact]
    public async Task Anonymous_is_redirected_to_login()
    {
        var client = StartServer().CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/любая");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // CookieAuthenticationHandler строит абсолютный Location (схема+хост), поэтому сравниваем путь.
        Assert.StartsWith("/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Login_page_is_open()
    {
        var response = await StartServer().CreateClient().GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // login.html строило свой site-словарь руками и теряло background_url — единственная
    // страница без фона, вопреки дизайну. Проверяем, что теперь она берёт SiteModel как все.
    [Fact]
    public async Task Login_page_carries_the_background_link_when_one_is_set()
    {
        var factory = StartServer();
        var folder = Path.Combine(dataRoot, "background");
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, "background.jpg"), [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<SiteSettings>().Set("theme.background", "background.jpg");

        var html = await factory.CreateClient().GetStringAsync("/login");

        Assert.Contains("<style>:root { --bg-image: url(\"/background?v=", html);
    }

    [Fact]
    public async Task Theme_asset_is_open_but_article_is_not()
    {
        var client = StartServer().CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var css = await client.GetAsync("/assets/style.css");
        var article = await client.GetAsync("/любая-статья");

        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal(HttpStatusCode.Found, article.StatusCode);
    }

    [Fact]
    public async Task Right_password_opens_the_site()
    {
        var factory = StartServer();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient();

        var response = await TestLogin.PostLogin(client, "aleks", "тайна");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    // Сервер всегда видит http от прокси Apache — Secure появляется только если прокси сказал X-Forwarded-Proto: https.
    [Fact]
    public async Task Forwarded_https_header_marks_session_cookie_secure()
    {
        var factory = StartServer();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await TestLogin.PostLogin(client, "aleks", "тайна");

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.Contains(cookies, cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Plain_http_request_gets_cookie_without_secure_and_login_still_works()
    {
        var factory = StartServer();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await TestLogin.PostLogin(client, "aleks", "тайна");

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(cookies, cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_shows_error_and_keeps_site_closed()
    {
        var factory = StartServer();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await TestLogin.PostLogin(client, "aleks", "мимо");

        Assert.Contains("Wrong login or password", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/")).StatusCode);
    }

    // Login CSRF: чужой сайт отправляет форму входа за жертву и впускает её в свою учётку.
    [Fact]
    public async Task Login_without_antiforgery_token_is_rejected()
    {
        var factory = StartServer();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var fields = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" });

        var response = await client.PostAsync("/login", fields);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Login_form_carries_the_antiforgery_field()
    {
        var html = await StartServer().CreateClient().GetStringAsync("/login");

        Assert.Matches(
            "<form class=\"login\" method=\"post\" action=\"/login\">\\s*<input type=\"hidden\" name=\"[^\"]+\" value=\"[^\"]+\">",
            html);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
