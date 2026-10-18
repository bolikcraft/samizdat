using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

/// Вход с паролем короче 8 знаков: демо admin/admin из миграции Init или старый пароль.
[Collection("db")]
public class SetupTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public SetupTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    WebApplicationFactory<Program> StartServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    static void AddPerson(WebApplicationFactory<Program> factory, string login, string password,
                          UserRole role = UserRole.Owner, bool approved = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login, PasswordHash = PasswordHasher.Hash(password), Role = role,
            CreatedAt = DateTimeOffset.UtcNow, ApprovedAt = approved ? DateTimeOffset.UtcNow : null,
        });
        db.SaveChanges();
    }

    static UserRow? FindUser(WebApplicationFactory<Program> factory, string login)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>().Users.AsNoTracking()
            .FirstOrDefault(row => row.Login == login);
    }

    // Без автоперехода: тесты проверяют сами редиректы.
    static async Task<HttpClient> SignIn(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var answer = await TestLogin.PostLogin(client, login, password);
        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return client;
    }

    // Токен берётся со страницы /setup: с меткой смены прочие страницы закрыты.
    static async Task<HttpResponseMessage> PostSetup(HttpClient client, string login, string password, string repeat)
    {
        var (name, value) = TestLogin.AntiforgeryToken(await client.GetStringAsync("/setup"));
        return await client.PostAsync("/setup", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [name] = value, ["login"] = login, ["password"] = password, ["repeat"] = repeat,
        }));
    }

    [Fact]
    public async Task Short_password_at_sign_in_leads_to_setup()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await TestLogin.PostLogin(client, "admin", "admin");

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/setup", answer.Headers.Location?.OriginalString);
    }

    // Проверка одобрения стоит раньше метки смены: короткий пароль не открывает /setup ждущему.
    [Fact]
    public async Task Short_password_of_a_pending_person_does_not_lead_to_setup()
    {
        var factory = StartServer();
        AddPerson(factory, "ivan", "parol", UserRole.Reader, approved: false);
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await TestLogin.PostLogin(client, "ivan", "parol");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("must approve your request", await answer.Content.ReadAsStringAsync());
        var cookies = answer.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        Assert.DoesNotContain(cookies, cookie => cookie.StartsWith(".AspNetCore.Cookies"));
        Assert.StartsWith("/login", (await client.GetAsync("/setup")).Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Long_password_at_sign_in_does_not_lead_to_setup()
    {
        var factory = StartServer();
        AddPerson(factory, "aleks", "dlinnyy-parol");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await TestLogin.PostLogin(client, "aleks", "dlinnyy-parol");

        Assert.Equal("/", answer.Headers.Location?.OriginalString);
        Assert.Equal("/", (await client.GetAsync("/setup")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Setup_page_offers_the_current_login()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = await SignIn(factory, "admin", "admin");

        var html = await client.GetStringAsync("/setup");

        Assert.Contains("action=\"/setup\"", html);
        Assert.Contains("name=\"login\" value=\"admin\"", html);
        Assert.Contains("action=\"/logout\"", html);
    }

    [Fact]
    public async Task Setup_saves_a_new_login_and_password_and_signs_in_again()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var before = FindUser(factory, "admin")!;
        var client = await SignIn(factory, "admin", "admin");

        var answer = await PostSetup(client, "aleks", "novyy-parol", "novyy-parol");

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/", answer.Headers.Location?.OriginalString);
        Assert.Null(FindUser(factory, "admin"));
        var after = FindUser(factory, "aleks")!;
        Assert.Equal(before.Id, after.Id);
        Assert.True(PasswordHasher.Verify("novyy-parol", after.PasswordHash));
        Assert.NotEqual(before.SessionStamp, after.SessionStamp);
        // Новая cookie — без метки смены: /setup уводит на главную.
        Assert.Equal("/", (await client.GetAsync("/setup")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Setup_keeps_the_login_when_it_is_not_changed()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = await SignIn(factory, "admin", "admin");

        var answer = await PostSetup(client, "admin", "novyy-parol", "novyy-parol");

        Assert.Equal("/", answer.Headers.Location?.OriginalString);
        Assert.True(PasswordHasher.Verify("novyy-parol", FindUser(factory, "admin")!.PasswordHash));
    }

    [Fact]
    public async Task Setup_refuses_a_short_password()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = await SignIn(factory, "admin", "admin");

        var answer = await PostSetup(client, "admin", "korotko", "korotko");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("The password needs 8 characters or more.", await answer.Content.ReadAsStringAsync());
        Assert.True(PasswordHasher.Verify("admin", FindUser(factory, "admin")!.PasswordHash));
    }

    [Fact]
    public async Task Setup_refuses_the_login_of_another_person()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        AddPerson(factory, "ivan", "parol-ivana", UserRole.Reader);
        var client = await SignIn(factory, "admin", "admin");

        var answer = await PostSetup(client, "IVAN", "novyy-parol", "novyy-parol");

        Assert.Contains("This login is in use.", await answer.Content.ReadAsStringAsync());
        Assert.True(PasswordHasher.Verify("admin", FindUser(factory, "admin")!.PasswordHash));
    }

    // Форма меняет пароль, не спрашивая текущий. Без метки смены она не должна работать.
    [Fact]
    public async Task Setup_form_without_the_mark_changes_nothing()
    {
        var factory = StartServer();
        AddPerson(factory, "aleks", "dlinnyy-parol");
        var client = await SignIn(factory, "aleks", "dlinnyy-parol");

        var answer = await TestLogin.Post(client, "/setup", new()
        {
            ["login"] = "drugoy", ["password"] = "novyy-parol", ["repeat"] = "novyy-parol",
        });

        Assert.Equal("/", answer.Headers.Location?.OriginalString);
        Assert.True(PasswordHasher.Verify("dlinnyy-parol", FindUser(factory, "aleks")!.PasswordHash));
    }

    [Fact]
    public async Task Other_sessions_with_the_mark_end_after_setup()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var first = await SignIn(factory, "admin", "admin");
        var second = await SignIn(factory, "admin", "admin");

        await PostSetup(first, "admin", "novyy-parol", "novyy-parol");

        var answer = await second.GetAsync("/setup");
        Assert.Equal(HttpStatusCode.Found, answer.StatusCode);
        Assert.StartsWith("/login", answer.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Pages_lead_to_setup_until_the_password_is_changed()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = await SignIn(factory, "admin", "admin");

        foreach (var path in new[] { "/", "/settings", "/search?q=server", "/lyubaya-statya", "/login" })
        {
            var answer = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
            Assert.Equal("/setup", answer.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task Api_and_forms_are_refused_until_the_password_is_changed()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = await SignIn(factory, "admin", "admin");
        var (name, value) = TestLogin.AntiforgeryToken(await client.GetStringAsync("/setup"));

        var api = await client.GetAsync("/api/state");
        var form = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                [name] = value, ["current"] = "admin", ["new"] = "novyy-parol", ["new2"] = "novyy-parol",
            }));

        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, form.StatusCode);
        Assert.True(PasswordHasher.Verify("admin", FindUser(factory, "admin")!.PasswordHash));
    }

    [Fact]
    public async Task Theme_files_core_scripts_background_and_logout_stay_open_during_setup()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = await SignIn(factory, "admin", "admin");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/assets/style.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_app/forms.js")).StatusCode);
        // Фона нет — 404 от самого маршрута, а не 302 от ворот.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/background")).StatusCode);

        var (name, value) = TestLogin.AntiforgeryToken(await client.GetStringAsync("/setup"));
        var logout = await client.PostAsync("/logout",
            new FormUrlEncodedContent(new Dictionary<string, string> { [name] = value }));
        Assert.Equal("/login", logout.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Pages_open_after_setup()
    {
        var factory = StartServer();
        AddPerson(factory, "admin", "admin");
        var client = await SignIn(factory, "admin", "admin");

        await PostSetup(client, "aleks", "novyy-parol", "novyy-parol");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings")).StatusCode);
    }
}
