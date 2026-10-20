using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

/// Формы с паролем: вход, смена своего пароля, пароль человека от владельца.
[Collection("db")]
public class PasswordFormTests : IDisposable
{
    static readonly string LongPassword = new('д', 201);

    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public PasswordFormTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    WebApplicationFactory<Program> StartServer(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        });

    static int AddPerson(WebApplicationFactory<Program> factory, string login, string password,
                         UserRole role = UserRole.Owner)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var person = new UserRow
        {
            Login = login, PasswordHash = PasswordHasher.Hash(password), Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(person);
        db.SaveChanges();
        return person.Id;
    }

    static UserRow? FindUser(WebApplicationFactory<Program> factory, string login)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>().Users.AsNoTracking()
            .FirstOrDefault(row => row.Login == login);
    }

    // Без автоперехода: тесты проверяют сами редиректы.
    static HttpClient NewClient(WebApplicationFactory<Program> factory)
        => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = NewClient(factory);
        var answer = await TestLogin.PostLogin(client, login, password);
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return client;
    }

    static Task<HttpResponseMessage> ChangePassword(HttpClient client, string current, string next)
        => TestLogin.Post(client, "/settings/password", new()
        {
            ["current"] = current, ["new"] = next, ["new2"] = next,
        });

    [Fact]
    public async Task Sign_in_with_a_password_longer_than_200_is_refused_without_a_hash()
    {
        var factory = StartServer(("Samizdat:Auth:ParallelHashes", "1"), ("Samizdat:Auth:HashQueue", "0"));
        AddPerson(factory, "aleks", "parol-aleksa");
        // Место под хэш занято: ответ «неверный пароль», а не 429, значит хэш не считался.
        using var held = await factory.Services.GetRequiredService<PasswordGate>().Enter(CancellationToken.None);
        Assert.True(held.IsAcquired);

        var answer = await TestLogin.PostLogin(NewClient(factory), "aleks", LongPassword);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("Wrong login or password", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task New_password_longer_than_200_is_refused()
    {
        var factory = StartServer();
        AddPerson(factory, "aleks", "parol-aleksa");
        var client = await SignIn(factory, "aleks", "parol-aleksa");

        var answer = await ChangePassword(client, "parol-aleksa", LongPassword);

        Assert.Equal("/settings?err=long_password#profile", answer.Headers.Location?.OriginalString);
        Assert.True(PasswordHasher.Verify("parol-aleksa", FindUser(factory, "aleks")!.PasswordHash));
    }

    [Fact]
    public async Task Current_password_longer_than_200_is_wrong()
    {
        var factory = StartServer();
        AddPerson(factory, "aleks", "parol-aleksa");
        var client = await SignIn(factory, "aleks", "parol-aleksa");

        var answer = await ChangePassword(client, LongPassword, "novyy-parol");

        Assert.Equal("/settings?err=wrong_password#profile", answer.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Long_password_message_names_the_limit()
    {
        var factory = StartServer();
        AddPerson(factory, "aleks", "parol-aleksa");
        var client = await SignIn(factory, "aleks", "parol-aleksa");

        var html = await client.GetStringAsync("/settings?err=long_password");

        Assert.Contains("The password is more than 200 characters.", html);
    }

    [Fact]
    public async Task Owner_does_not_give_a_person_a_password_longer_than_200()
    {
        var factory = StartServer();
        AddPerson(factory, "hozyain", "parol-hozyaina");
        var ivan = AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var owner = await SignIn(factory, "hozyain", "parol-hozyaina");

        var added = await TestLogin.Post(owner, "/settings/people",
            new() { ["login"] = "petr", ["password"] = LongPassword });
        var reset = await TestLogin.Post(owner, $"/settings/people/{ivan}/password",
            new() { ["password"] = LongPassword });

        Assert.Equal("/settings?err=person_long_password#users", added.Headers.Location?.OriginalString);
        Assert.Equal("/settings?err=person_long_password#users", reset.Headers.Location?.OriginalString);
        Assert.Null(FindUser(factory, "petr"));
        Assert.True(PasswordHasher.Verify("parol-ivana", FindUser(factory, "ivan")!.PasswordHash));
    }

    // Место под хэш одно, очереди нет: пока тест держит место, форма обязана получить 429.
    [Fact]
    public async Task Password_change_waits_for_a_free_hash_slot()
    {
        var factory = StartServer(("Samizdat:Auth:ParallelHashes", "1"), ("Samizdat:Auth:HashQueue", "0"));
        AddPerson(factory, "aleks", "parol-aleksa");
        var client = await SignIn(factory, "aleks", "parol-aleksa");
        var held = await factory.Services.GetRequiredService<PasswordGate>().Enter(CancellationToken.None);
        Assert.True(held.IsAcquired);

        var busy = await ChangePassword(client, "parol-aleksa", "novyy-parol");
        held.Dispose();
        var free = await ChangePassword(client, "parol-aleksa", "novyy-parol");

        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Equal("/settings?ok=password#profile", free.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Owner_forms_with_a_password_wait_for_a_free_hash_slot()
    {
        var factory = StartServer(("Samizdat:Auth:ParallelHashes", "1"), ("Samizdat:Auth:HashQueue", "0"));
        AddPerson(factory, "hozyain", "parol-hozyaina");
        var ivan = AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var owner = await SignIn(factory, "hozyain", "parol-hozyaina");
        using var held = await factory.Services.GetRequiredService<PasswordGate>().Enter(CancellationToken.None);
        Assert.True(held.IsAcquired);

        var added = await TestLogin.Post(owner, "/settings/people",
            new() { ["login"] = "petr", ["password"] = "parol-petra" });
        var reset = await TestLogin.Post(owner, $"/settings/people/{ivan}/password",
            new() { ["password"] = "novyy-parol" });

        Assert.Equal(HttpStatusCode.TooManyRequests, added.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, reset.StatusCode);
        Assert.Null(FindUser(factory, "petr"));
        Assert.True(PasswordHasher.Verify("parol-ivana", FindUser(factory, "ivan")!.PasswordHash));
    }

    const string TooMany = "Too many attempts.";

    // Предел по адресу выключен: тест проверяет только счётчик логина.
    WebApplicationFactory<Program> StartWithFailures(int perLogin, params (string Key, string Value)[] more)
        => StartServer([("Samizdat:Auth:FailuresPerLogin", perLogin.ToString()),
                        ("Samizdat:Auth:AttemptsPerMinute", "0"), .. more]);

    [Fact]
    public async Task Sign_in_is_refused_after_too_many_wrong_passwords_even_with_the_right_one()
    {
        var factory = StartWithFailures(2);
        AddPerson(factory, "aleks", "parol-aleksa");
        var client = NewClient(factory);

        await TestLogin.PostLogin(client, "aleks", "mimo-mimo");
        await TestLogin.PostLogin(client, "aleks", "mimo-mimo");
        var refused = await TestLogin.PostLogin(client, "aleks", "parol-aleksa");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains(TooMany, await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Refused_sign_in_does_not_calculate_a_hash()
    {
        var factory = StartWithFailures(1, ("Samizdat:Auth:ParallelHashes", "1"), ("Samizdat:Auth:HashQueue", "0"));
        AddPerson(factory, "aleks", "parol-aleksa");
        await TestLogin.PostLogin(NewClient(factory), "aleks", "mimo-mimo");
        // Место под хэш занято: пустой 429 от PasswordGate значил бы, что форма пошла считать хэш.
        using var held = await factory.Services.GetRequiredService<PasswordGate>().Enter(CancellationToken.None);
        Assert.True(held.IsAcquired);

        var refused = await TestLogin.PostLogin(NewClient(factory), "aleks", "parol-aleksa");

        Assert.Contains(TooMany, await refused.Content.ReadAsStringAsync());
    }

    // Логины в базе сравниваются без регистра, поэтому и счётчик у них общий.
    [Fact]
    public async Task Login_in_another_case_shares_the_counter()
    {
        var factory = StartWithFailures(2);
        AddPerson(factory, "aleks", "parol-aleksa");

        await TestLogin.PostLogin(NewClient(factory), "aleks", "mimo-mimo");
        await TestLogin.PostLogin(NewClient(factory), "ALEKS", "mimo-mimo");
        var refused = await TestLogin.PostLogin(NewClient(factory), "Aleks", "parol-aleksa");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task Unknown_login_is_counted_too()
    {
        var factory = StartWithFailures(1);

        var first = await TestLogin.PostLogin(NewClient(factory), "nikto", "mimo-mimo");
        var second = await TestLogin.PostLogin(NewClient(factory), "nikto", "mimo-mimo");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains(TooMany, await second.Content.ReadAsStringAsync());
    }

    // Такие пароли не стоят Argon2id: если бы они шли в счёт, поток мусорных логинов
    // дёшево вытеснял бы из таблицы счётчик жертвы.
    [Fact]
    public async Task Empty_and_too_long_passwords_are_not_counted()
    {
        var factory = StartWithFailures(2);
        AddPerson(factory, "aleks", "parol-aleksa");

        for (var i = 0; i < 3; i++)
        {
            var empty = await TestLogin.PostLogin(NewClient(factory), "aleks", "");
            var tooLong = await TestLogin.PostLogin(NewClient(factory), "aleks", LongPassword);
            Assert.Contains("Wrong login or password", await empty.Content.ReadAsStringAsync());
            Assert.Contains("Wrong login or password", await tooLong.Content.ReadAsStringAsync());
        }
        var right = await TestLogin.PostLogin(NewClient(factory), "aleks", "parol-aleksa");

        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
    }

    [Fact]
    public async Task Successful_sign_in_does_not_reset_the_counter()
    {
        var factory = StartWithFailures(2);
        AddPerson(factory, "aleks", "parol-aleksa");

        await TestLogin.PostLogin(NewClient(factory), "aleks", "mimo-mimo");
        var right = await TestLogin.PostLogin(NewClient(factory), "aleks", "parol-aleksa");
        await TestLogin.PostLogin(NewClient(factory), "aleks", "mimo-mimo");
        var refused = await TestLogin.PostLogin(NewClient(factory), "aleks", "parol-aleksa");

        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_in_settings_counts_for_the_login()
    {
        var factory = StartWithFailures(2);
        AddPerson(factory, "aleks", "parol-aleksa");
        var client = await SignIn(factory, "aleks", "parol-aleksa");

        await ChangePassword(client, "mimo-mimo", "novyy-parol");
        await ChangePassword(client, "mimo-mimo", "novyy-parol");
        var refused = await ChangePassword(client, "parol-aleksa", "novyy-parol");

        Assert.Equal("/settings?err=too_many_attempts#profile", refused.Headers.Location?.OriginalString);
        Assert.True(PasswordHasher.Verify("parol-aleksa", FindUser(factory, "aleks")!.PasswordHash));
        Assert.Contains(TooMany, await client.GetStringAsync("/settings?err=too_many_attempts"));
        var signIn = await TestLogin.PostLogin(NewClient(factory), "aleks", "parol-aleksa");
        Assert.Equal(HttpStatusCode.TooManyRequests, signIn.StatusCode);
    }

    [Fact]
    public async Task Zero_turns_the_counter_off()
    {
        var factory = StartWithFailures(0);
        AddPerson(factory, "aleks", "parol-aleksa");

        for (var i = 0; i < 5; i++) await TestLogin.PostLogin(NewClient(factory), "aleks", "mimo-mimo");
        var right = await TestLogin.PostLogin(NewClient(factory), "aleks", "parol-aleksa");

        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
    }
}
