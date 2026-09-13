using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class SiteSettingsTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public SiteSettingsTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartFactory(string? theme = null, Action<IServiceCollection>? configureServices = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            if (theme is not null) builder.UseSetting("Samizdat:Theme", theme);
            if (configureServices is not null) builder.ConfigureServices(configureServices);
        });

    HttpClient LoginClient(WebApplicationFactory<Program> factory)
    {
        var login = $"owner-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Users.Add(new UserRow
            {
                Login = login,
                PasswordHash = PasswordHasher.Hash("тайна"),
                Role = UserRole.Owner,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        var client = factory.CreateClient();
        client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = "тайна" })).Wait();
        return client;
    }

    void WriteArticle(string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);
    }

    void WriteThemeFile(string themeName, string relativePath, string text)
    {
        var path = Path.Combine(dataRoot, "themes", themeName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    void Register(WebApplicationFactory<Program> factory, string slug, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    static void SetSetting(WebApplicationFactory<Program> factory, string key, string value)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set(key, value);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public void Config_value_is_used_while_the_database_is_empty()
    {
        var factory = StartFactory(theme: "custom-cfg");

        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();

        Assert.Equal("custom-cfg", settings.ThemeName);
    }

    [Fact]
    public void Database_value_overrides_config()
    {
        var factory = StartFactory(theme: "custom-cfg");
        SetSetting(factory, "theme.name", "custom-db");

        // EF Core кэширует DbContext по scope — резолвим SiteSettings в новом, а не в том, где писали.
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();

        Assert.Equal("custom-db", settings.ThemeName);
    }

    [Fact]
    public async Task Unknown_theme_falls_back_to_the_built_in_one_and_logs_a_warning()
    {
        var logger = new CapturingLoggerProvider();
        var factory = StartFactory(configureServices: services => services.AddSingleton<ILoggerProvider>(logger));
        SetSetting(factory, "theme.name", "нет-такой-темы");
        var client = LoginClient(factory);

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("id=\"nav-tree\"", html);
        Assert.Contains(logger.Messages, message => message.Contains("нет-такой-темы"));
    }

    [Fact]
    public async Task Switching_theme_rebuilds_the_page_instead_of_serving_the_cached_one()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "privet", "Привет");
        var client = LoginClient(factory);

        var before = await client.GetStringAsync("/privet");
        Assert.DoesNotContain("marker-theme-two", before);

        WriteThemeFile("имя2", "article.html", "marker-theme-two<h1>{{ article.title }}</h1>{{ article.html }}");
        SetSetting(factory, "theme.name", "имя2");

        var after = await client.GetStringAsync("/privet");

        Assert.Contains("marker-theme-two", after);
    }

    [Fact]
    public async Task Owner_menu_is_shown_with_login_settings_link_and_logout_form()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "privet", "Привет");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("href=\"/settings\"", html);
        Assert.Contains("<form method=\"post\" action=\"/logout\">", html);
    }

    [Fact]
    public async Task Login_page_has_no_owner_menu()
    {
        var factory = StartFactory();
        var client = factory.CreateClient();

        var html = await client.GetStringAsync("/login");

        Assert.DoesNotContain("class=\"user-menu\"", html);
        Assert.DoesNotContain("action=\"/logout\"", html);
    }

    [Fact]
    public async Task Color_scheme_defaults_to_system_and_can_be_overridden_from_the_database()
    {
        var factory = StartFactory();
        var client = LoginClient(factory);

        var before = await client.GetStringAsync("/");
        Assert.Contains("data-color-scheme=\"system\"", before);

        SetSetting(factory, "theme.color_scheme", "dark");
        var after = await client.GetStringAsync("/");

        Assert.Contains("data-color-scheme=\"dark\"", after);
    }

    [Fact]
    public void View_fingerprint_stays_the_same_when_nothing_changed()
    {
        var factory = StartFactory();
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();

        // Не Guid.NewGuid() и не что-то ещё случайное: тот же вид — тот же отпечаток,
        // иначе он не годится в ключ кэша, который проверяет Task 5.
        var first = settings.ViewFingerprint;
        var second = settings.ViewFingerprint;
        Assert.Equal(first, second);
    }

    [Fact]
    public void View_fingerprint_stays_the_same_across_requests()
    {
        var factory = StartFactory();

        // Кэш страниц смотрит на отпечаток из разных запросов — каждый в своём scope
        // и со своим DbContext, поэтому стабильность внутри одного scope тут не показатель.
        using var first = factory.Services.CreateScope();
        var fingerprint = first.ServiceProvider.GetRequiredService<SiteSettings>().ViewFingerprint;

        using var second = factory.Services.CreateScope();
        Assert.Equal(fingerprint, second.ServiceProvider.GetRequiredService<SiteSettings>().ViewFingerprint);
    }

    [Fact]
    public void View_fingerprint_changes_with_the_color_scheme()
    {
        var factory = StartFactory();
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();
        var before = settings.ViewFingerprint;

        settings.Set("theme.color_scheme", "dark");

        Assert.NotEqual(before, settings.ViewFingerprint);
    }

    [Fact]
    public void View_fingerprint_changes_with_the_theme_name()
    {
        var factory = StartFactory();
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();
        var before = settings.ViewFingerprint;

        settings.Set("theme.name", "another-theme");

        Assert.NotEqual(before, settings.ViewFingerprint);
    }

    [Fact]
    public void View_fingerprint_changes_when_a_background_is_saved()
    {
        var factory = StartFactory();
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();
        var background = scope.ServiceProvider.GetRequiredService<BackgroundFile>();
        var before = settings.ViewFingerprint;

        var name = background.Save(new MemoryStream(Jpeg()), ".jpg");
        settings.Set("theme.background", name);

        Assert.NotEqual(before, settings.ViewFingerprint);
    }

    [Fact]
    public void Background_url_is_null_until_a_background_is_saved()
    {
        var factory = StartFactory();
        using var scope = factory.Services.CreateScope();

        Assert.Null(scope.ServiceProvider.GetRequiredService<SiteSettings>().BackgroundUrl);
    }

    [Fact]
    public void Background_url_points_at_the_saved_picture_and_changes_when_it_is_replaced()
    {
        var factory = StartFactory();
        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SiteSettings>();
        var background = scope.ServiceProvider.GetRequiredService<BackgroundFile>();

        var name = background.Save(new MemoryStream(Jpeg()), ".jpg");
        var path = Path.Combine(dataRoot, "background", name);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc));
        settings.Set("theme.background", name);

        var first = settings.BackgroundUrl;
        Assert.Matches(@"^/background\?v=\d+$", first);

        // Save кладёт файл под тем же именем — версия должна замениться, а не подтвердиться.
        background.Save(new MemoryStream(Jpeg()), ".jpg");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 13, 11, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(first, settings.BackgroundUrl);
    }

    static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose()
        {
        }

        sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                     Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning) owner.Messages.Add(formatter(state, exception));
            }
        }
    }
}
