using System.Text.RegularExpressions;
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

    // Без автоперехода тест видит сам адрес возврата: за ним стоит код итога, ради которого
    // форма и отправлялась.
    static async Task<HttpClient> LoginClient(WebApplicationFactory<Program> factory, string login,
                                              bool followRedirects = true)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = followRedirects,
        });
        await TestLogin.PostLogin(client, login, Password);
        return client;
    }

    static async Task<HttpClient> LoginAsOwner(WebApplicationFactory<Program> factory, string login = "aleks")
    {
        AddUser(factory, login, UserRole.Owner);
        return await LoginClient(factory, login);
    }

    static async Task<HttpClient> LoginAsReader(WebApplicationFactory<Program> factory, string login, string language,
                                                bool followRedirects = true)
    {
        AddUser(factory, login, UserRole.Reader);
        SetUserLanguage(factory, login, language);
        return await LoginClient(factory, login, followRedirects);
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static async Task<HttpResponseMessage> PostLanguage(HttpClient client, string path, string language)
    {
        var match = Regex.Match(await client.GetStringAsync("/settings"),
                                "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");

        return await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [match.Groups[1].Value] = match.Groups[2].Value,
            ["language"] = language,
        }));
    }

    async Task AddArticle(WebApplicationFactory<Program> factory, string slug)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.md"), "---\ntitle: Ёж\n---\n\nТекст.\n");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = "Ёж", ContentHash = $"hash-{slug}",
            Visibility = ArticleVisibility.Shared, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
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
    public async Task Page_title_speaks_the_chosen_language()
    {
        var factory = StartFactory();
        var client = await LoginAsOwner(factory);

        Assert.Contains("<title>Settings</title>", await client.GetStringAsync("/settings"));
    }

    [Fact]
    public async Task Search_title_carries_the_query()
    {
        var factory = StartFactory();
        var client = await LoginAsOwner(factory);

        Assert.Contains("<title>Search: hedgehog</title>", await client.GetStringAsync("/search?q=hedgehog"));
    }

    [Fact]
    public async Task Settings_message_speaks_the_chosen_language()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "ru");
        var client = await LoginAsOwner(factory);

        Assert.Contains("Неверный текущий пароль", await client.GetStringAsync("/settings?err=wrong_password"));
    }

    [Fact]
    public async Task People_list_names_the_role_in_the_site_language()
    {
        var factory = StartFactory();
        var client = await LoginAsOwner(factory);

        var page = await client.GetStringAsync("/settings");

        Assert.Contains("Owner", page);
        Assert.DoesNotContain("владелец", page);
    }

    [Fact]
    public async Task Login_error_speaks_the_site_language()
    {
        var factory = StartFactory();
        AddUser(factory, "aleks", UserRole.Owner);

        var answer = await TestLogin.PostLogin(factory.CreateClient(), "aleks", "не та");

        Assert.Contains("Wrong login or password", await answer.Content.ReadAsStringAsync());
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

    [Fact]
    public async Task Cached_article_does_not_leak_into_another_language()
    {
        var factory = StartFactory();
        await AddArticle(factory, "ezh");
        var english = await LoginAsReader(factory, "reader-en", "en");
        var russian = await LoginAsReader(factory, "reader-ru", "ru");

        var first = await english.GetStringAsync("/ezh");
        var second = await russian.GetStringAsync("/ezh");

        Assert.Contains("lang=\"en\"", first);
        Assert.Contains("lang=\"ru\"", second);
    }

    [Fact]
    public async Task Owner_and_reader_each_get_the_article_in_his_own_language()
    {
        var factory = StartFactory();
        await AddArticle(factory, "ezh");
        AddUser(factory, "aleks", UserRole.Owner);
        SetUserLanguage(factory, "aleks", "ru");
        var owner = await LoginClient(factory, "aleks");
        var reader = await LoginAsReader(factory, "reader-en", "en");

        Assert.Contains("lang=\"ru\"", await owner.GetStringAsync("/ezh"));
        Assert.Contains("lang=\"en\"", await reader.GetStringAsync("/ezh"));
    }

    [Fact]
    public async Task Article_is_drawn_again_after_the_person_changes_his_language()
    {
        var factory = StartFactory();
        await AddArticle(factory, "ezh");
        var reader = await LoginAsReader(factory, "reader", "en");
        Assert.Contains("lang=\"en\"", await reader.GetStringAsync("/ezh"));

        SetUserLanguage(factory, "reader", "ru");

        Assert.Contains("lang=\"ru\"", await reader.GetStringAsync("/ezh"));
    }

    [Fact]
    public async Task Owner_sets_the_site_language_from_the_settings_page()
    {
        var factory = StartFactory();
        var client = await LoginAsOwner(factory);

        await PostLanguage(client, "/settings/appearance", "ru");

        Assert.Contains("lang=\"ru\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task Reader_sees_the_language_section()
    {
        var factory = StartFactory();
        var reader = await LoginAsReader(factory, "reader", "");

        var page = await reader.GetStringAsync("/settings");

        Assert.Contains("action=\"/settings/language\"", page);
        Assert.DoesNotContain("action=\"/settings/appearance\"", page);
    }

    [Fact]
    public async Task Reader_sets_a_personal_language_and_the_site_stays_as_it_was()
    {
        var factory = StartFactory();
        var reader = await LoginAsReader(factory, "reader", "");

        await PostLanguage(reader, "/settings/language", "ru");

        Assert.Contains("lang=\"ru\"", await reader.GetStringAsync("/"));
        Assert.Contains("lang=\"en\"", await (await LoginAsOwner(factory)).GetStringAsync("/"));
    }

    [Fact]
    public async Task Personal_language_set_back_to_empty_follows_the_site_again()
    {
        var factory = StartFactory();
        SetSiteLanguage(factory, "ru");
        var reader = await LoginAsReader(factory, "reader", "");
        await PostLanguage(reader, "/settings/language", "en");
        Assert.Contains("lang=\"en\"", await reader.GetStringAsync("/"));

        await PostLanguage(reader, "/settings/language", "");

        Assert.Contains("lang=\"ru\"", await reader.GetStringAsync("/"));
    }

    [Fact]
    public async Task Unknown_language_in_the_form_is_refused()
    {
        var factory = StartFactory();
        var reader = await LoginAsReader(factory, "reader", "", followRedirects: false);

        var answer = await PostLanguage(reader, "/settings/language", "нет-такого");

        Assert.Contains("err=bad_language", answer.Headers.Location!.OriginalString);
        Assert.Contains("#profile", answer.Headers.Location!.OriginalString);
    }
}
