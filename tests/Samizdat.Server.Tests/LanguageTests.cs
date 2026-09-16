using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class LanguageTests : IDisposable
{
    const string Password = "тайна";

    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public LanguageTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    static void AddUser(WebApplicationFactory<Program> factory, string login, UserRole role)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = login, PasswordHash = PasswordHasher.Hash(Password), Role = role,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    static void SetSiteLanguage(WebApplicationFactory<Program> factory, string code)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set("site.language", code);
    }

    static void SetUserLanguage(WebApplicationFactory<Program> factory, string login, string code)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Single(row => row.Login == login).Language = code;
        db.SaveChanges();
    }

    static async Task<HttpClient> LoginClient(WebApplicationFactory<Program> factory, string login)
    {
        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = Password }));
        return client;
    }

    static async Task<HttpClient> LoginAsOwner(WebApplicationFactory<Program> factory, string login = "aleks")
    {
        AddUser(factory, login, UserRole.Owner);
        return await LoginClient(factory, login);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task Site_speaks_english_out_of_the_box()
    {
        var factory = StartFactory();
        var client = await LoginAsOwner(factory);

        Assert.Contains("lang=\"en\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Site_language_setting_changes_the_page()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "ru");
        var client = await LoginAsOwner(factory);

        Assert.Contains("lang=\"ru\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Personal_language_wins_over_the_site_one()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "en");
        AddUser(factory, "aleks", UserRole.Owner);
        SetUserLanguage(factory, "aleks", "ru");
        var client = await LoginClient(factory, "aleks");

        Assert.Contains("lang=\"ru\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Empty_personal_language_follows_the_site()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "ru");
        var client = await LoginAsOwner(factory);

        Assert.Contains("lang=\"ru\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Unknown_language_in_settings_does_not_break_the_page()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "кто-то-стёр-пакет");
        var client = await LoginAsOwner(factory);

        Assert.Contains("lang=\"en\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Unknown_personal_language_falls_back_to_the_site_one()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "ru");
        AddUser(factory, "aleks", UserRole.Owner);
        SetUserLanguage(factory, "aleks", "нет-такого");
        var client = await LoginClient(factory, "aleks");

        Assert.Contains("lang=\"ru\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Login_page_speaks_the_site_language()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "ru");

        Assert.Contains("lang=\"ru\"", await factory.CreateClient().GetStringAsync("/login"));
    }

    [Fact]
    public async Task A_language_pack_on_disk_adds_a_new_language()
    {
        var folder = Path.Combine(dataRoot, "lang");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "de.json"), """{"language.name": "Deutsch"}""");
        var factory = StartFactory();
        SetSiteLanguage(factory, "de");
        var client = await LoginAsOwner(factory);

        Assert.Contains("lang=\"de\"", await client.GetStringAsync("/"));
    }
}
