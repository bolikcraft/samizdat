using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Commands;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ApiTokenTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    WebApplicationFactory<Program> StartServer() =>
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

    string CreateToken(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var owner = new UserRow
        {
            Login = $"owner-{Guid.NewGuid():N}",
            PasswordHash = PasswordHasher.Hash("x"),
            Role = UserRole.Owner,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(owner);
        db.SaveChanges();

        var token = ApiToken.Create();
        db.ApiTokens.Add(new ApiTokenRow
        {
            UserId = owner.Id,
            TokenHash = ApiToken.HashOf(token),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return token;
    }

    [Fact]
    public async Task Api_without_token_returns_401()
    {
        var response = await StartServer().CreateClient().GetAsync("/api/state");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_with_wrong_token_returns_401()
    {
        var client = StartServer().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "мимо");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Api_with_right_token_works()
    {
        var factory = StartServer();
        var token = CreateToken(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public void Stored_token_is_a_hash_not_the_token()
    {
        var token = ApiToken.Create();

        Assert.NotEqual(token, ApiToken.HashOf(token));
        Assert.Equal(ApiToken.HashOf(token), ApiToken.HashOf(token));
    }

    [Fact]
    public async Task Right_token_updates_last_used_at()
    {
        var factory = StartServer();
        var token = CreateToken(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.GetAsync("/api/state");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.ApiTokens.Single(item => item.TokenHash == ApiToken.HashOf(token));
        Assert.NotNull(row.LastUsedAt);
    }

    // Cookie и токен — две отдельные схемы: сессия владельца не должна открывать /api.
    [Fact]
    public async Task Owner_cookie_does_not_open_api()
    {
        var factory = StartServer();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
    }

    // И наоборот: токен CLI не должен открывать страницы сайта.
    [Fact]
    public async Task Api_token_does_not_open_pages()
    {
        var factory = StartServer();
        var token = CreateToken(factory);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Token_new_without_owner_fails_gracefully()
    {
        database.ResetDatabase();
        var factory = StartServer();

        var handled = await ServerCommands.TryRun(["token", "new"], factory.Services);

        Assert.True(handled);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
