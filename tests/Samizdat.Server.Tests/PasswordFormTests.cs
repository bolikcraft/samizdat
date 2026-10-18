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
}
