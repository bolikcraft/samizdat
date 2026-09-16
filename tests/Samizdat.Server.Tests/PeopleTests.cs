using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class PeopleTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public PeopleTests(DatabaseFixture database) => this.database = database;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task Owner_adds_a_person_who_can_then_log_in()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var owner = await Login(factory, "hozyain", "parol");
        var answer = await Post(owner, "/settings/people",
            new() { ["login"] = "ivan", ["password"] = "parol-ivana" });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?ok=person_added#users", answer.Headers.Location?.ToString());
        Assert.Equal(UserRole.Reader, Users(factory).Single(row => row.Login == "ivan").Role);

        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "ivan", "parol-ivana")).StatusCode);
    }

    [Fact]
    public async Task Reader_sees_only_the_password_section()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol");
        var html = await client.GetStringAsync("/settings/");

        Assert.Contains("/settings/password", html);
        Assert.DoesNotContain("/settings/people", html);
        Assert.DoesNotContain("/settings/appearance", html);
        Assert.DoesNotContain("/settings/background", html);
        Assert.DoesNotContain("/settings/tokens", html);
        Assert.DoesNotContain("/settings/signup", html);
        Assert.DoesNotContain("/settings/invites", html);
    }

    [Fact]
    public async Task Owner_sees_every_section()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var html = await client.GetStringAsync("/settings/");

        Assert.Contains("/settings/password", html);
        Assert.Contains("/settings/people", html);
        Assert.Contains("/settings/appearance", html);
        Assert.Contains("Users", html);
        Assert.Contains("/settings/invites", html);
        Assert.Contains("Registration", html);
    }

    [Fact]
    public async Task Busy_login_is_refused_with_a_message()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);

        var owner = await Login(factory, "hozyain", "parol");
        var answer = await Post(owner, "/settings/people",
            new() { ["login"] = "ivan", ["password"] = "drugoy-parol" });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?err=login_taken#users", answer.Headers.Location?.ToString());
        Assert.Single(Users(factory), row => row.Login == "ivan");

        // Пароль занятого логина не тронут: отказ не должен менять чужую учётку.
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "ivan", "parol-ivana")).StatusCode);
    }

    [Fact]
    public async Task Login_that_differs_only_in_case_is_busy_too()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);

        var owner = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(owner, "/settings/people",
            new() { ["login"] = "Ivan", ["password"] = "drugoy-parol" });

        Assert.Equal("/settings?err=login_taken#users", answer.Headers.Location?.ToString());
        Assert.Single(Users(factory), row => row.Login == "ivan");
    }

    [Fact]
    public async Task Person_logs_in_with_a_login_in_another_case()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);

        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "IVAN", "parol-ivana")).StatusCode);
    }

    [Fact]
    public async Task Login_of_a_new_person_is_not_empty()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);

        var owner = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(owner, "/settings/people",
            new() { ["login"] = "  ", ["password"] = "parol-ivana" });

        Assert.Equal("/settings?err=bad_person#users", answer.Headers.Location?.ToString());
        Assert.Single(Users(factory));
    }

    [Fact]
    public async Task Short_password_of_a_new_person_is_refused()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);

        var owner = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(owner, "/settings/people",
            new() { ["login"] = "ivan", ["password"] = "korotko" });

        Assert.Equal("/settings?err=person_short_password#users", answer.Headers.Location?.ToString());
        Assert.DoesNotContain(Users(factory), row => row.Login == "ivan");
    }

    [Fact]
    public async Task Short_new_password_of_a_person_is_refused()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var ivan = AddPerson(factory, "ivan", "staryy-parol", UserRole.Reader);

        var owner = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(owner, $"/settings/people/{ivan}/password",
            new() { ["password"] = "korotko" });

        Assert.Equal("/settings?err=person_short_password#users", answer.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "ivan", "staryy-parol")).StatusCode);
    }

    [Fact]
    public async Task Person_who_is_not_there_is_not_found()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);

        var owner = await Login(factory, "hozyain", "parol-hozyaina");

        Assert.Equal(HttpStatusCode.NotFound, (await Post(owner, "/settings/people/4242/delete", [])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Post(owner, "/settings/people/4242/password",
                new() { ["password"] = "dlinnyy-parol" })).StatusCode);
    }

    [Fact]
    public async Task Owner_changes_the_password_of_a_person()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        var ivan = AddPerson(factory, "ivan", "staryy-parol", UserRole.Reader);

        var owner = await Login(factory, "hozyain", "parol");
        var answer = await Post(owner, $"/settings/people/{ivan}/password",
            new() { ["password"] = "novyy-parol" });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?ok=person_password#users", answer.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.OK, (await TryLogin(factory, "ivan", "staryy-parol")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "ivan", "novyy-parol")).StatusCode);
    }

    [Fact]
    public async Task Owner_deletes_a_person_and_their_tokens()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        var ivan = AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        AddToken(factory, ivan);

        var owner = await Login(factory, "hozyain", "parol");
        var answer = await Post(owner, $"/settings/people/{ivan}/delete", []);

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?ok=person_deleted#users", answer.Headers.Location?.ToString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.Users.Where(row => row.Login == "ivan"));
        Assert.Empty(db.ApiTokens.Where(row => row.UserId == ivan));
    }

    // Ждущего эта кнопка не должна снести в обход очереди: его место — раздел «Регистрация».
    [Fact]
    public async Task A_pending_person_is_not_deleted_from_the_people_table()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        var gost = AddPendingPerson(factory, "gost");

        var owner = await Login(factory, "hozyain", "parol");
        var answer = await Post(owner, $"/settings/people/{gost}/delete", []);

        Assert.Equal("/settings?err=not_pending#users", answer.Headers.Location?.ToString());
        Assert.Contains(Users(factory), row => row.Login == "gost");
    }

    [Fact]
    public async Task Last_owner_is_not_deleted()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var owner = AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol");
        var answer = await Post(client, $"/settings/people/{owner}/delete", []);

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?err=last_owner#users", answer.Headers.Location?.ToString());
        Assert.Single(Users(factory), row => row.Login == "hozyain");
    }

    [Fact]
    public async Task Owner_does_not_delete_themselves()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var owner = AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        AddPerson(factory, "vtoroy", "parol-vtorogo", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(client, $"/settings/people/{owner}/delete", []);

        Assert.Equal("/settings?err=self_delete#users", answer.Headers.Location?.ToString());
        Assert.Equal(2, Users(factory).Count);
    }

    [Fact]
    public async Task Owner_does_not_delete_another_owner()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var other = AddPerson(factory, "vtoroy", "parol-vtorogo", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(client, $"/settings/people/{other}/delete", []);

        Assert.Equal("/settings?err=other_owner#users", answer.Headers.Location?.ToString());
        Assert.Single(Users(factory), row => row.Login == "vtoroy");
    }

    [Fact]
    public async Task Owner_does_not_change_the_password_of_another_owner()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var other = AddPerson(factory, "vtoroy", "parol-vtorogo", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(client, $"/settings/people/{other}/password",
            new() { ["password"] = "chuzhoy-parol" });

        Assert.Equal("/settings?err=other_owner#users", answer.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "vtoroy", "parol-vtorogo")).StatusCode);
    }

    [Fact]
    public async Task Owner_does_not_change_their_own_password_from_the_table()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var owner = AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);

        var client = await Login(factory, "hozyain", "parol-hozyaina");
        var answer = await Post(client, $"/settings/people/{owner}/password",
            new() { ["password"] = "novyy-parol" });

        Assert.Equal("/settings?err=own_password#users", answer.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "hozyain", "parol-hozyaina")).StatusCode);
    }

    [Fact]
    public async Task Table_shows_buttons_only_for_readers()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var owner = AddPerson(factory, "hozyain", "parol-hozyaina", UserRole.Owner);
        var other = AddPerson(factory, "vtoroy", "parol-vtorogo", UserRole.Owner);
        var ivan = AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);

        var client = await Login(factory, "hozyain", "parol-hozyaina");
        var html = await client.GetStringAsync("/settings/");

        Assert.Contains($"/settings/people/{ivan}/password", html);
        Assert.Contains($"/settings/people/{ivan}/delete", html);
        Assert.DoesNotContain($"/settings/people/{owner}/", html);
        Assert.DoesNotContain($"/settings/people/{other}/", html);
    }

    [Fact]
    public async Task Deleted_person_loses_their_session()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        var ivan = AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol-ivana");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings/")).StatusCode);

        var owner = await Login(factory, "hozyain", "parol");
        await Post(owner, $"/settings/people/{ivan}/delete", []);

        var settings = await client.GetAsync("/settings/");
        Assert.Equal(HttpStatusCode.Found, settings.StatusCode);
        Assert.StartsWith("/login", settings.Headers.Location?.PathAndQuery);
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task New_password_from_the_owner_loses_the_session()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "hozyain", "parol", UserRole.Owner);
        var ivan = AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);

        var client = await Login(factory, "ivan", "parol-ivana");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings/")).StatusCode);

        var owner = await Login(factory, "hozyain", "parol");
        await Post(owner, $"/settings/people/{ivan}/password", new() { ["password"] = "novyy-parol" });

        var answer = await client.GetAsync("/settings/");
        Assert.Equal(HttpStatusCode.Found, answer.StatusCode);
        Assert.StartsWith("/login", answer.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Own_new_password_keeps_the_session()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "staryy-parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "staryy-parol");
        await Post(client, "/settings/password", new()
        {
            ["current"] = "staryy-parol", ["new"] = "novyy-parol", ["new2"] = "novyy-parol",
        });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings/")).StatusCode);
    }

    [Fact]
    public async Task Reader_changes_their_own_password()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "staryy-parol", UserRole.Reader);

        var client = await Login(factory, "ivan", "staryy-parol");
        var answer = await Post(client, "/settings/password", new()
        {
            ["current"] = "staryy-parol", ["new"] = "novyy-parol", ["new2"] = "novyy-parol",
        });

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/settings?ok=password#profile", answer.Headers.Location?.ToString());
        Assert.Equal(HttpStatusCode.Redirect, (await TryLogin(factory, "ivan", "novyy-parol")).StatusCode);
    }

    [Fact]
    public async Task Reader_does_not_reach_the_owner_routes()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddPerson(factory, "ivan", "parol", UserRole.Reader);
        var other = AddPerson(factory, "hozyain", "parol", UserRole.Owner);

        var client = await Login(factory, "ivan", "parol");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(client, "/settings/people",
                new() { ["login"] = "petr", ["password"] = "parol-petra" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(client, "/settings/appearance", new() { ["theme"] = "default" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post(client, $"/settings/people/{other}/delete", [])).StatusCode);

        Assert.DoesNotContain(Users(factory), row => row.Login == "petr");
        Assert.Single(Users(factory), row => row.Login == "hozyain");
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

    static List<UserRow> Users(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        return db.Users.AsNoTracking().ToList();
    }

    static int AddPerson(WebApplicationFactory<Program> factory, string login, string password, UserRole role)
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

    static int AddPendingPerson(WebApplicationFactory<Program> factory, string login)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var person = new UserRow
        {
            Login = login, PasswordHash = PasswordHasher.Hash("parol-gostya"), Role = UserRole.Reader,
            CreatedAt = DateTimeOffset.UtcNow, ApprovedAt = null,
        };
        db.Users.Add(person);
        db.SaveChanges();
        return person.Id;
    }

    static void AddToken(WebApplicationFactory<Program> factory, int userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.ApiTokens.Add(new ApiTokenRow
        {
            UserId = userId, TokenHash = ApiToken.HashOf(ApiToken.Create()), CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/"));
        fields[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
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

    /// Вход, который может и не удаться: неверный пароль возвращает форму с 200, верный — редирект.
    static async Task<HttpResponseMessage> TryLogin(WebApplicationFactory<Program> factory,
                                                    string login, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
    }
}
