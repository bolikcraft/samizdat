using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class SettingsPageTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public SettingsPageTests(DatabaseFixture database)
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

    async Task<HttpClient> LoginClient(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = factory.CreateClient();
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
        return client;
    }

    void WriteArticle(string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);
    }

    void RegisterArticle(WebApplicationFactory<Program> factory, string slug, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    void WriteThemeFile(string themeName, string relativePath, string text)
    {
        var path = Path.Combine(dataRoot, "themes", themeName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    void PutBackground(WebApplicationFactory<Program> factory, byte[] bytes, string name)
    {
        var folder = Path.Combine(dataRoot, "background");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set("theme.background", name);
    }

    void RemoveBackground(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BackgroundFile>().Remove();
        scope.ServiceProvider.GetRequiredService<SiteSettings>().Set("theme.background", "");
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task Anonymous_is_redirected_to_login()
    {
        var client = StartFactory().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/settings");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("/login", response.Headers.Location?.PathAndQuery);
    }

    [Fact]
    public async Task Owner_sees_the_settings_form()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var response = await client.GetAsync("/settings");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/settings/password\"", html);
        Assert.Contains("action=\"/settings/appearance\"", html);
        Assert.Contains("action=\"/settings/background\"", html);
        Assert.Contains("enctype=\"multipart/form-data\"", html);
        Assert.Contains("name=\"file\"", html);
        Assert.Contains("token new", html);
    }

    [Fact]
    public async Task The_settings_page_puts_the_section_list_where_the_article_tree_is()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var html = await client.GetStringAsync("/settings");

        // Настройки идут в той же раскладке, что и статьи: боковик плюс панель, а не одна колонка.
        Assert.Contains("class=\"shell\"", html);
        Assert.DoesNotContain("shell-plain", html);
        Assert.DoesNotContain("nav-tree", html);
        Assert.Contains("class=\"settings-nav\"", html);
    }

    [Fact]
    public async Task Every_section_of_the_list_has_its_own_pane()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var html = await client.GetStringAsync("/settings");

        // Раздел выбирается якорем. Ссылка без своей панели оставила бы страницу пустой.
        var sections = Regex.Matches(html, "href=\"#([a-z]+)\"").Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(["appearance", "articles", "security", "language", "people", "signup", "tokens", "links"],
                     sections);
        foreach (var section in sections)
            Assert.Contains($"class=\"settings-pane\" id=\"{section}\"", html);
    }

    [Fact]
    public async Task A_saved_form_returns_to_its_own_section()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var appearance = await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["theme"] = "default", ["color_scheme"] = "dark" }));
        var password = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["current"] = "неверно", ["new"] = "x", ["new2"] = "x" }));

        Assert.Equal("/settings?ok=appearance#appearance", appearance.Headers.Location?.OriginalString);
        Assert.Equal("/settings?err=wrong_password#security", password.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_message_pops_up_once_over_the_sections()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        // Разделы переключаются якорем, без перезагрузки. Сообщение стоит вне разделов и гаснет само,
        // поэтому не висит над чужой формой.
        var background = await client.GetStringAsync("/settings?ok=background");
        Assert.Contains("<p class=\"toast toast-ok\" role=\"status\">The background is set.</p>", background);
        Assert.True(background.IndexOf("class=\"toast", StringComparison.Ordinal)
                    < background.IndexOf("class=\"settings-panes\"", StringComparison.Ordinal));

        var wrong = await client.GetStringAsync("/settings?err=wrong_password");
        Assert.Contains("class=\"toast toast-err\" role=\"alert\"", wrong);
    }

    [Fact]
    public async Task The_uploaded_picture_and_its_remove_button_appear_only_while_a_file_is_on_disk()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var before = await client.GetStringAsync("/settings");
        Assert.DoesNotContain("value=\"upload\"", before);
        Assert.DoesNotContain("action=\"/settings/background/remove\"", before);

        PutBackground(factory, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4], "background.jpg");
        var withBackground = await client.GetStringAsync("/settings");
        Assert.Contains("value=\"upload\"", withBackground);
        Assert.Contains("action=\"/settings/background/remove\"", withBackground);

        RemoveBackground(factory);
        var after = await client.GetStringAsync("/settings");
        Assert.DoesNotContain("value=\"upload\"", after);
        Assert.DoesNotContain("action=\"/settings/background/remove\"", after);
    }

    [Fact]
    public async Task A_picture_from_the_theme_set_becomes_the_background_and_is_marked_in_the_gallery()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/background/pick", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["pick"] = "preset:dawn.svg" }));

        Assert.Contains("--bg-image: url(\"/assets/backgrounds/dawn.svg\")", await client.GetStringAsync("/"));
        // Отмечена ровно одна плитка — выбранная.
        var settings = await client.GetStringAsync("/settings");
        Assert.Contains("value=\"preset:dawn.svg\"", settings);
        Assert.Single(Regex.Matches(settings, "bg-tile-current"));
    }

    [Fact]
    public async Task A_color_from_the_palette_paints_the_backdrop_and_the_accent()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/background/pick", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["pick"] = "color:#3f7a6a" }));

        var page = await client.GetStringAsync("/");
        // Из --brand стили считают акцент, из --backdrop — подложку страницы.
        Assert.Contains("--backdrop: #3f7a6a", page);
        Assert.Contains("--brand: #3f7a6a", page);
        Assert.DoesNotContain("--bg-image", page);
    }

    [Fact]
    public async Task A_background_outside_the_theme_set_is_refused()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        foreach (var pick in new[] { "preset:../../etc/passwd", "preset:нет-такой.svg", "color:#000000", "чепуха" })
        {
            var response = await client.PostAsync("/settings/background/pick", new FormUrlEncodedContent(
                new Dictionary<string, string> { [name] = value, ["pick"] = pick }));

            Assert.Contains("err=background_unknown", response.RequestMessage!.RequestUri!.ToString());
        }

        using var scope = factory.Services.CreateScope();
        Assert.Equal(BackgroundKind.None, scope.ServiceProvider.GetRequiredService<SiteSettings>().Background.Kind);
    }

    [Fact]
    public async Task Correct_current_password_changes_it_and_new_password_works_next_login()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                [name] = value, ["current"] = "тайна", ["new"] = "новыйпарольок", ["new2"] = "новыйпарольок",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ok=password", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "новыйпарольок" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_shows_error_and_keeps_old_password_working()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                [name] = value, ["current"] = "неверно", ["new"] = "новыйпарольок", ["new2"] = "новыйпарольок",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("err=wrong_password", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task New_password_shorter_than_8_chars_is_rejected()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["current"] = "тайна", ["new"] = "коротко", ["new2"] = "коротко" }));

        Assert.Contains("err=short_password", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Mismatched_new_passwords_are_rejected()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                [name] = value, ["current"] = "тайна", ["new"] = "новыйпарольок", ["new2"] = "другойпароль",
            }));

        Assert.Contains("err=password_mismatch", response.RequestMessage!.RequestUri!.ToString());

        var checkClient = factory.CreateClient();
        await checkClient.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        Assert.Equal(HttpStatusCode.OK, (await checkClient.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Choosing_theme_changes_rendered_html()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\nтекст\n");
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        RegisterArticle(factory, "privet", "Привет");
        WriteThemeFile("имя2", "article.html", "marker-imya2<h1>{{ article.title }}</h1>{{ article.html }}");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["theme"] = "имя2", ["color_scheme"] = "system" }));
        var html = await client.GetStringAsync("/privet");

        Assert.Contains("marker-imya2", html);
    }

    [Fact]
    public async Task Choosing_color_scheme_is_reflected_in_html_attribute()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["theme"] = "default", ["color_scheme"] = "dark" }));
        var html = await client.GetStringAsync("/");

        Assert.Contains("data-color-scheme=\"dark\"", html);
    }

    [Fact]
    public async Task Token_list_shows_note_and_last_used_at()
    {
        var factory = StartFactory();
        var owner = AddOwner(factory, "aleks", "тайна");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.ApiTokens.Add(new ApiTokenRow
            {
                UserId = owner.Id,
                TokenHash = ApiToken.HashOf(ApiToken.Create()),
                Note = "ноутбук",
                CreatedAt = DateTimeOffset.UtcNow,
                LastUsedAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero),
            });
            db.SaveChanges();
        }
        var client = await LoginClient(factory, "aleks", "тайна");

        var html = await client.GetStringAsync("/settings");

        Assert.Contains("ноутбук", html);
        Assert.Contains("2026-09-01", html);
    }

    [Fact]
    public async Task A_token_made_in_the_browser_is_shown_once_and_opens_the_api()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/tokens", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["note"] = "ноутбук" }));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = Regex.Match(html, "class=\"new-token-value\" type=\"text\" readonly value=\"([0-9a-f]+)\"")
            .Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.Contains("ноутбук", html);

        var apiClient = factory.CreateClient();
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/api/state")).StatusCode);

        // База хранит только хеш, поэтому второй раз показать токен нечем.
        Assert.DoesNotContain(token, await client.GetStringAsync("/settings"));
    }

    [Fact]
    public async Task A_token_made_without_a_note_gets_no_note()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/tokens", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["note"] = "   " }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Null(db.ApiTokens.Single().Note);
    }

    [Fact]
    public async Task Making_a_token_without_antiforgery_token_is_rejected()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var response = await client.PostAsync("/settings/tokens", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["note"] = "чужой" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.ApiTokens);
    }

    [Fact]
    public async Task A_token_note_can_be_changed_and_cleared()
    {
        var factory = StartFactory();
        var owner = AddOwner(factory, "aleks", "тайна");
        int tokenId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var row = new ApiTokenRow
            {
                UserId = owner.Id, TokenHash = ApiToken.HashOf(ApiToken.Create()), Note = "ноутбук",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ApiTokens.Add(row);
            db.SaveChanges();
            tokenId = row.Id;
        }
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync($"/settings/tokens/{tokenId}/note", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["note"] = "рабочая машина" }));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            Assert.Equal("рабочая машина", db.ApiTokens.Single().Note);
        }

        await client.PostAsync($"/settings/tokens/{tokenId}/note", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["note"] = "" }));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            Assert.Null(db.ApiTokens.Single().Note);
        }
    }

    [Fact]
    public async Task A_note_of_a_foreign_token_stays_as_it_was()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var other = AddOwner(factory, "other", "другая");
        int foreignTokenId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var row = new ApiTokenRow
            {
                UserId = other.Id, TokenHash = ApiToken.HashOf(ApiToken.Create()), Note = "чужой",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ApiTokens.Add(row);
            db.SaveChanges();
            foreignTokenId = row.Id;
        }
        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync($"/settings/tokens/{foreignTokenId}/note",
            new FormUrlEncodedContent(new Dictionary<string, string> { [name] = value, ["note"] = "мой" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var check = factory.Services.CreateScope();
        var tokens = check.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Equal("чужой", tokens.ApiTokens.Single().Note);
    }

    [Fact]
    public async Task Revoking_token_closes_its_api_access()
    {
        var factory = StartFactory();
        var owner = AddOwner(factory, "aleks", "тайна");
        var apiToken = ApiToken.Create();
        int tokenId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var row = new ApiTokenRow
            {
                UserId = owner.Id, TokenHash = ApiToken.HashOf(apiToken), Note = "cli",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ApiTokens.Add(row);
            db.SaveChanges();
            tokenId = row.Id;
        }

        var apiClient = factory.CreateClient();
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/api/state")).StatusCode);

        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync($"/settings/tokens/{tokenId}/revoke", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await apiClient.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Revoking_missing_or_foreign_token_returns_404_without_details()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var other = AddOwner(factory, "other", "другая");
        int foreignTokenId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var row = new ApiTokenRow
            {
                UserId = other.Id, TokenHash = ApiToken.HashOf(ApiToken.Create()), CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ApiTokens.Add(row);
            db.SaveChanges();
            foreignTokenId = row.Id;
        }

        var client = await LoginClient(factory, "aleks", "тайна");
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var missing = await client.PostAsync("/settings/tokens/999999/revoke", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));
        var foreign = await client.PostAsync($"/settings/tokens/{foreignTokenId}/revoke", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Empty(await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Password_change_without_antiforgery_token_is_rejected()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var response = await client.PostAsync("/settings/password", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["current"] = "тайна", ["new"] = "новыйпарольок", ["new2"] = "новыйпарольок" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Logout_without_antiforgery_token_is_rejected_but_with_token_succeeds()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var withoutToken = await client.PostAsync("/logout", new FormUrlEncodedContent(
            new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, withoutToken.StatusCode);

        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/"));
        var response = await client.PostAsync("/logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/login\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task The_site_title_out_of_the_box_is_samizdat()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var html = await client.GetStringAsync("/");

        Assert.Contains("<title>Samizdat</title>", html);
        Assert.Contains("<span class=\"top-title\">Samizdat</span>", html);
    }

    [Fact]
    public async Task The_owner_renames_the_site_and_the_header_and_the_tab_follow()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["title"] = "  Записки <Алекса>  " }));
        var index = await client.GetStringAsync("/");
        var settings = await client.GetStringAsync("/settings");

        Assert.Equal("/settings?ok=appearance#appearance", response.Headers.Location?.OriginalString);
        Assert.Contains("<title>Записки &lt;Алекса&gt;</title>", index);
        Assert.Contains("<span class=\"top-title\">Записки &lt;Алекса&gt;</span>", index);
        Assert.Contains("name=\"title\" value=\"Записки &lt;Алекса&gt;\"", settings);
        Assert.Contains("maxlength=\"60\"", settings);
        Assert.Contains("<small id=\"site-title-hint\" class=\"field-hint\">Up to 60 characters.</small>", settings);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task An_empty_site_title_leaves_only_the_icon_in_the_header(string title)
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var response = await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["title"] = title }));
        var index = await client.GetStringAsync("/");
        var settings = await client.GetStringAsync("/settings");

        Assert.Equal("/settings?ok=appearance#appearance", response.Headers.Location?.OriginalString);
        // Без текста ссылку на главную называет сама иконка, а вкладке нужно хоть какое-то имя.
        Assert.Matches("<a class=\"top-home\" href=\"/\"><img src=\"/assets/icon.svg\" alt=\"Samizdat\"[^>]*></a>", index);
        Assert.DoesNotContain("top-title", index);
        Assert.Contains("<title>Samizdat</title>", index);
        Assert.Contains("name=\"title\" value=\"\"", settings);
        Assert.DoesNotMatch("name=\"title\"[^>]*required", settings);
    }

    [Fact]
    public async Task A_site_title_longer_than_the_limit_is_refused()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        var tooLong = await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["title"] = new string('я', SiteSettings.MaxTitleLength + 1) }));
        Assert.Equal("/settings?err=bad_title#appearance", tooLong.Headers.Location?.OriginalString);
        Assert.Contains("<title>Samizdat</title>", await client.GetStringAsync("/"));
        Assert.Contains("The site name must not be longer than 60 characters.",
                        await client.GetStringAsync("/settings?err=bad_title"));

        var exact = new string('я', SiteSettings.MaxTitleLength);
        var fits = await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["title"] = exact }));
        Assert.Equal("/settings?ok=appearance#appearance", fits.Headers.Location?.OriginalString);
        Assert.Contains($"<title>{exact}</title>", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_title_of_emoji_counts_characters_not_code_units()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        // Каждый знак тут — две половинки UTF-16: по string.Length строка вдвое длиннее предела.
        var title = string.Concat(Enumerable.Repeat("📚", SiteSettings.MaxTitleLength));
        var response = await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["title"] = title }));

        Assert.Equal("/settings?ok=appearance#appearance", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_header_shows_the_icon_before_the_title()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        var html = await client.GetStringAsync("/");

        Assert.Matches("<a class=\"top-home\" href=\"/\"><img src=\"/assets/icon.svg\" alt=\"\"[^>]*>"
                       + "<span class=\"top-title\">Samizdat</span></a>", html);
    }

    [Fact]
    public async Task The_home_page_shows_the_site_title_out_of_the_box()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = await LoginClient(factory, "aleks", "тайна");

        Assert.Contains("<h1>Samizdat</h1>", await client.GetStringAsync("/"));
        Assert.Contains("name=\"show_title\" value=\"on\" checked", await client.GetStringAsync("/settings"));
    }

    [Fact]
    public async Task The_owner_hides_the_site_title_on_the_home_page_and_the_header_keeps_it()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        // Снятый флажок форма не присылает: есть только название.
        var response = await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["title"] = "Записки" }));
        var index = await client.GetStringAsync("/");
        var settings = await client.GetStringAsync("/settings");

        Assert.Equal("/settings?ok=appearance#appearance", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain("<h1>", index);
        Assert.Contains("<span class=\"top-title\">Записки</span>", index);
        Assert.Contains("name=\"show_title\" value=\"on\">", settings);

        await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["title"] = "Записки", ["show_title"] = "on" }));

        Assert.Contains("<h1>Записки</h1>", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task A_form_without_the_title_field_leaves_the_show_title_switch_alone()
    {
        var factory = StartFactory();
        AddOwner(factory, "aleks", "тайна");
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = "aleks", ["password"] = "тайна" }));
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/settings"));

        await client.PostAsync("/settings/appearance", new FormUrlEncodedContent(
            new Dictionary<string, string> { [name] = value, ["color_scheme"] = "dark" }));

        Assert.Contains("<h1>Samizdat</h1>", await client.GetStringAsync("/"));
    }
}
