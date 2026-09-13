using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class SettingsPageTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public SettingsPageTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
        });

    UserRow AddOwner(WebApplicationFactory<Program> factory, string login, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var owner = new UserRow
        {
            Login = login,
            PasswordHash = PasswordHasher.Hash(password),
            Role = UserRole.Owner,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(owner);
        db.SaveChanges();
        return owner;
    }

    async Task<HttpClient> LoginClient(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
        return client;
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

    void WriteThemeFile(string themeName, string relativePath, string text)
    {
        var path = Path.Combine(dataRoot, "themes", themeName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task Anonymous_is_redirected_to_login()
    {
        var client = StartFactory().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Owner_sees_the_settings_form()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var response = await client.GetAsync("/settings");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/settings/password\"", html);
        Assert.Contains("action=\"/settings/appearance\"", html);
        Assert.Contains("token new", html);
    }

    [Fact]
    public async Task Correct_current_password_changes_it_and_new_password_works_next_login()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                [name] = value, ["current"] = "тайна", ["new"] = "новыйпарольок", ["new2"] = "новыйпарольок",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok=password", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "новыйпарольок" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_shows_error_and_keeps_old_password_working()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                [name] = value, ["current"] = "неверно", ["new"] = "новыйпарольок", ["new2"] = "новыйпарольок",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("err=wrong_password", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task New_password_shorter_than_8_chars_is_rejected()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["current"] = "тайна", ["new"] = "коротко", ["new2"] = "коротко" }));

        Assert.Contains("err=short_password", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Mismatched_new_passwords_are_rejected()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                [name] = value, ["current"] = "тайна", ["new"] = "новыйпарольок", ["new2"] = "другойпароль",
            }));

        Assert.Contains("err=password_mismatch", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Choosing_theme_changes_rendered_html()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\nтекст\n");
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        RegisterArticle(factory, "privet", "Привет");
        WriteThemeFile("имя2", "article.html", "marker-imya2<h1>{{ article.title }}</h1>{{ article.html }}");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["theme"] = "имя2", ["color_scheme"] = "system" }));
        var html = await client.GetStringAsync("/privet");

        Assert.Contains("marker-imya2", html);
    }

    [Fact]
    public async Task Choosing_color_scheme_is_reflected_in_html_attribute()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["theme"] = "default", ["color_scheme"] = "dark" }));
        var html = await client.GetStringAsync("/");

        Assert.Contains("data-color-scheme=\"dark\"", html);
    }

    [Fact]
    public async Task Token_list_shows_note_and_last_used_at()
    {
        var factory = StartFactory();
        var owner = AddOwner(factory, "aleks", "тайна");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.ApiTokens.Add(new ApiTokenRow
            {
                UserId = owner.Id,
                TokenHash = ApiToken.HashOf(ApiToken.Create()),
                Note = "ноутбук",
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            });
            db.SaveChanges();
        }
        var client = await LoginClient(factory, "aleks", "тайна");

        var html = await client.GetStringAsync("/settings");

        Assert.Contains("ноутбук", html);
        Assert.Contains("2026-09-01", html);
    }

    [Fact]
    public async Task Revoking_token_closes_its_api_access()
    {
        var factory = StartFactory();
        var owner = AddOwner(factory, "aleks", "тайна");
        var apiToken = ApiToken.Create();
        int tokenId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var row = new ApiTokenRow
            {
                UserId = owner.Id, TokenHash = ApiToken.HashOf(apiToken), Note = "cli",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ApiTokens.Add(row);
            db.SaveChanges();
            tokenId = row.Id;
        }

        var apiClient = factory.CreateClient();
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/api/state")).StatusCode);

        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync($"/settings/tokens/{tokenId}/revoke", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await apiClient.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Revoking_missing_or_foreign_token_returns_404_without_details()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var other = AddOwner(factory, "other", "другая");
        int foreignTokenId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var row = new ApiTokenRow
            {
                UserId = other.Id, TokenHash = ApiToken.HashOf(ApiToken.Create()), CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ApiTokens.Add(row);
            db.SaveChanges();
            foreignTokenId = row.Id;
        }

        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var missing = await client.PostAsync("/settings/tokens/999999/revoke", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));
        var foreign = await client.PostAsync($"/settings/tokens/{foreignTokenId}/revoke", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Password_change_without_antiforgery_token_is_rejected()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["current"] = "тайна", ["new"] = "новыйпарольок", ["new2"] = "новыйпарольок" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_without_antiforgery_token_is_rejected_but_with_token_succeeds()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var withoutToken = await client.PostAsync("/logout", new FormUrlEncodedContent(
            new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);

        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/"));
        var response = await client.PostAsync("/logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/login\"", await client.GetStringAsync("/"));
    }
}
