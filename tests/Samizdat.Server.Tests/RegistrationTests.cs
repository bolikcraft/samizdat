using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class RegistrationTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public RegistrationTests(DatabaseFixture database) => this.database = database;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public void Invite_is_alive_until_it_is_used_revoked_or_expired()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(new InviteRow { Token = "t" }.IsAlive(now));
        Assert.True(new InviteRow { Token = "t", ExpiresAt = now.AddDays(1) }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", ExpiresAt = now.AddDays(-1) }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", RevokedAt = now }.IsAlive(now));
        Assert.False(new InviteRow { Token = "t", UsedAt = now }.IsAlive(now));
    }

    [Fact]
    public void A_person_made_by_hand_is_approved_at_once()
    {
        Assert.NotNull(new UserRow { Login = "ivan", PasswordHash = "x" }.ApprovedAt);
    }

    [Fact]
    public async Task A_person_waiting_for_approval_cannot_log_in()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPending(factory, "gost", "parol-gostya");

        var answer = await TryLogin(factory, "gost", "parol-gostya");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("Заявка ещё не одобрена", await answer.Content.ReadAsStringAsync());
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

    static void AddPerson(WebApplicationFactory<Program> factory, string login, string password,
                          UserRole role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login,
            PasswordHash = PasswordHasher.Hash(password),
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    static void AddPending(WebApplicationFactory<Program> factory, string login, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login,
            PasswordHash = PasswordHasher.Hash(password),
            Role = UserRole.Reader,
            CreatedAt = DateTimeOffset.UtcNow,
            ApprovedAt = null,
        });
        db.SaveChanges();
    }

    static List<UserRow> Users(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>()
            .Users.AsNoTracking().ToList();
    }

    static List<InviteRow> Invites(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>()
            .Invites.AsNoTracking().ToList();
    }

    /// Вход, который может и не удаться: неверный пароль возвращает форму с 200, верный — редирект.
    static async Task<HttpResponseMessage> TryLogin(WebApplicationFactory<Program> factory,
                                                    string login, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
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

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings/"));
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
