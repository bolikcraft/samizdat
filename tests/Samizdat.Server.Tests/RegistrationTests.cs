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

    [Fact]
    public async Task An_invite_makes_a_reader_who_is_let_in_at_once()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: "для Ивана");

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var answer = await client.PostAsync($"/i/{token}", Fields("ivan", "parol-ivana"));

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/", answer.Headers.Location?.ToString());

        var person = Users(factory).Single(row => row.Login == "ivan");
        Assert.Equal(UserRole.Reader, person.Role);
        Assert.NotNull(person.ApprovedAt);

        var invite = Invites(factory).Single();
        Assert.NotNull(invite.UsedAt);
        Assert.Equal("ivan", invite.UsedByLogin);
    }

    [Fact]
    public async Task The_same_invite_does_not_work_twice()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/i/{token}", Fields("ivan", "parol-ivana"));

        var again = await client.PostAsync($"/i/{token}", Fields("petr", "parol-petra"));

        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Single(Users(factory));
    }

    [Fact]
    public async Task An_expired_or_revoked_invite_shows_a_dead_page()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var expired = AddInvite(factory, note: null, expiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var revoked = AddInvite(factory, note: null, revokedAt: DateTimeOffset.UtcNow);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/i/{expired}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/i/{revoked}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/i/takogo-net")).StatusCode);
    }

    // Два человека открыли одну ссылку разом. Гашение с условием внутри UPDATE обязано пустить
    // только одного: без него оба проходили проверку IsAlive и заводили по учётке.
    [Fact]
    public async Task Two_people_racing_for_one_invite_give_one_reader()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var first = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var second = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answers = await Task.WhenAll(
            first.PostAsync($"/i/{token}", Fields("ivan", "parol-ivana")),
            second.PostAsync($"/i/{token}", Fields("petr", "parol-petra")));

        Assert.Single(Users(factory));
        Assert.Equal(1, answers.Count(answer => answer.StatusCode == HttpStatusCode.Redirect));
    }

    [Fact]
    public async Task A_busy_login_keeps_the_invite_alive()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var token = AddInvite(factory, note: null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.PostAsync($"/i/{token}", Fields("ivan", "drugoy-parol"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("логин уже занят", await answer.Content.ReadAsStringAsync());
        Assert.Null(Invites(factory).Single().UsedAt);
    }

    [Theory]
    [InlineData("ivan", "korotko", "korotko", "не короче")]
    [InlineData("ivan", "parol-ivana", "drugoy-parol", "не совпадают")]
    [InlineData("", "parol-ivana", "parol-ivana", "не должен быть пустым")]
    public async Task A_bad_form_shows_the_reason_and_keeps_the_invite(string login, string password,
                                                                      string repeat, string expected)
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var token = AddInvite(factory, note: null);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.PostAsync($"/i/{token}", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["login"] = login, ["password"] = password, ["repeat"] = repeat,
            }));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains(expected, await answer.Content.ReadAsStringAsync());
        Assert.Empty(Users(factory));
        Assert.Null(Invites(factory).Single().UsedAt);
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

    static FormUrlEncodedContent Fields(string login, string password) => new(
        new Dictionary<string, string>
        {
            ["login"] = login, ["password"] = password, ["repeat"] = password,
        });

    static string AddInvite(WebApplicationFactory<Program> factory, string? note,
                            DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var invite = new InviteRow
        {
            Token = ShareToken.Create(),
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
        };
        db.Invites.Add(invite);
        db.SaveChanges();
        return invite.Token;
    }
}
