using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class AuthTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    WebApplicationFactory<Program> StartServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
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

        var response = await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_shows_error_and_keeps_site_closed()
    {
        var factory = StartServer();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "мимо" }));

        Assert.Contains("Неверный", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/")).StatusCode);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
