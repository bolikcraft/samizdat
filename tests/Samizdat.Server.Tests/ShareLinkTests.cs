using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ShareLinkTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public ShareLinkTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
        });

    async Task<HttpClient> LoginClient(WebApplicationFactory<Program> factory, UserRole role = UserRole.Owner)
    {
        var login = $"owner-{Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Users.Add(new UserRow
            {
                Login = login,
                PasswordHash = PasswordHasher.Hash("тайна"),
                Role = role,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        // Без автоперехода: тесты проверяют сам редирект после POST. Вход это не ломает —
        // cookie ставится уже на первом ответе.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/login", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["login"] = login, ["password"] = "тайна" }));
        return client;
    }

    // Bearer-клиент на тот же factory: нужен, чтобы прогнать PUT /api/articles рядом с cookie-чтением страницы.
    HttpClient StartApiClient(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var owner = new UserRow
        {
            Login = $"owner-{Guid.NewGuid():N}",
            PasswordHash = PasswordHasher.Hash("тайна"),
            Role = UserRole.Owner,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(owner);
        db.SaveChanges();

        var token = ApiToken.Create();
        db.ApiTokens.Add(new ApiTokenRow
        {
            UserId = owner.Id,
            TokenHash = ApiToken.HashOf(token),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    void WriteArticle(string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);
    }

    void WriteAttachment(string slug, string name, byte[] bytes)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name), bytes);
    }

    void RegisterArticle(WebApplicationFactory<Program> factory, string slug, string title,
                         ArticleVisibility visibility = ArticleVisibility.Private)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = Guid.NewGuid().ToString("N"),
            Visibility = visibility, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    string AddLink(WebApplicationFactory<Program> factory, string slug,
                   DateTimeOffset? expiresAt = null, DateTimeOffset? revokedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var link = new ShareLinkRow
        {
            Token = ShareToken.Create(),
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt,
            RevokedAt = revokedAt,
        };
        db.ShareLinks.Add(link);
        db.SaveChanges();
        return link.Token;
    }

    ShareLinkRow LinkByToken(WebApplicationFactory<Program> factory, string token)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        return db.ShareLinks.First(row => row.Token == token);
    }

    static ShareLinkRow SingleLink(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>().ShareLinks.Single();
    }

    static List<ShareLinkRow> AllLinks(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SamizdatDbContext>().ShareLinks.ToList();
    }

    static void Expire(WebApplicationFactory<Program> factory, int id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.ShareLinks.Find(id)!.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
        db.SaveChanges();
    }

    // Токен берём с главной, а не со статьи: закрытую статью читатель не откроет, а поле там то же.
    static async Task<HttpResponseMessage> PostForm(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var antiforgery = AntiforgeryToken(await client.GetStringAsync("/"));
        fields[antiforgery.Name] = antiforgery.Value;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    static Task<HttpResponseMessage> PostShare(HttpClient client, string slug, string days, string note = "") =>
        PostForm(client, "/share", new() { ["slug"] = slug, ["days"] = days, ["note"] = note });

    static Task<HttpResponseMessage> PostRevoke(HttpClient client, string slug) =>
        PostForm(client, "/share/revoke", new() { ["slug"] = slug });

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public void Token_is_short_and_unique()
    {
        var first = ShareToken.Create();
        var second = ShareToken.Create();

        Assert.Equal(22, first.Length);
        Assert.NotEqual(first, second);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
    }

    [Fact]
    public void Deleting_an_article_deletes_its_links()
    {
        using var factory = StartFactory();
        RegisterArticle(factory, "statya", "Статья");
        var token = AddLink(factory, "statya");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Remove(db.Articles.Find("statya")!);
            db.SaveChanges();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            Assert.Empty(db.ShareLinks.Where(row => row.Token == token));
        }
    }

    [Fact]
    public async Task Live_link_shows_the_article_to_anonymous()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "---\ntitle: Про ежей\n---\n\nТекст статьи.");
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var token = AddLink(factory, "statya");

        var client = factory.CreateClient();
        var response = await client.GetAsync($"/s/{token}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Текст статьи.", html);
        Assert.Contains("Про ежей", html);
    }

    [Fact]
    public async Task Guest_page_has_no_navigation_and_no_owner_menu()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        RegisterArticle(factory, "chuzhaya", "Чужая статья");
        var token = AddLink(factory, "statya");

        var html = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.DoesNotContain("nav-tree", html);
        Assert.DoesNotContain("user-menu", html);
        Assert.Contains("noindex", html);
    }

    [Fact]
    public async Task Guest_page_does_not_show_other_slugs()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "см. [[chuzhaya]]");
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        WriteArticle("chuzhaya", "Чужой текст.");
        RegisterArticle(factory, "chuzhaya", "Чужая статья");
        var token = AddLink(factory, "statya");
        var owner = await LoginClient(factory);

        var guestPage = await factory.CreateClient().GetStringAsync($"/s/{token}");
        var ownerPage = await owner.GetStringAsync("/statya");

        // У владельца та же вики-ссылка ведёт на статью — значит гостевой html проверяем не впустую.
        Assert.Contains("href=\"/chuzhaya\"", ownerPage);
        Assert.DoesNotContain("href=\"/chuzhaya\"", guestPage);
    }

    [Fact]
    public async Task Guest_answers_are_never_stored_by_caches()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        WriteAttachment("statya", "shema.png", [1]);
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var live = AddLink(factory, "statya");
        var revoked = AddLink(factory, "statya", revokedAt: DateTimeOffset.UtcNow);

        var client = factory.CreateClient();
        var page = await client.GetAsync($"/s/{live}");
        var picture = await client.GetAsync($"/s/{live}/shema.png");
        var gone = await client.GetAsync($"/s/{revoked}");
        var missing = await client.GetAsync("/s/ZZZZZZZZZZZZZZZZZZZZZZ");

        foreach (var response in new[] { page, picture, gone, missing })
            Assert.True(response.Headers.CacheControl?.NoStore == true,
                        $"{response.RequestMessage!.RequestUri} -> {response.StatusCode}");
    }

    // Slug статьи может начинаться со слова share: ключи кэша владельца и гостя не должны пересечься.
    [Fact]
    public async Task Guest_page_keeps_its_own_cache_entry_next_to_a_share_named_article()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст для гостя.");
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        WriteArticle("share:statya", "Текст для владельца.");
        RegisterArticle(factory, "share:statya", "Одноимённая");
        var token = AddLink(factory, "statya");
        var owner = await LoginClient(factory);

        var ownerPage = await owner.GetStringAsync("/share:statya");
        var guestPage = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.Contains("Текст для владельца.", ownerPage);
        Assert.DoesNotContain("Текст для гостя.", ownerPage);
        Assert.Contains("Текст для гостя.", guestPage);
        Assert.DoesNotContain("Текст для владельца.", guestPage);
    }

    [Fact]
    public async Task Expired_and_revoked_links_answer_410()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var expired = AddLink(factory, "statya", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var revoked = AddLink(factory, "statya", revokedAt: DateTimeOffset.UtcNow);

        var client = factory.CreateClient();
        var first = await client.GetAsync($"/s/{expired}");
        var second = await client.GetAsync($"/s/{revoked}");

        Assert.Equal(HttpStatusCode.Gone, first.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
        Assert.Contains("больше не работает", await first.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_token_answers_404()
    {
        using var factory = StartFactory();

        var response = await factory.CreateClient().GetAsync("/s/ZZZZZZZZZZZZZZZZZZZZZZ");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Opening_the_page_counts_the_visit()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var token = AddLink(factory, "statya");

        var client = factory.CreateClient();
        await client.GetAsync($"/s/{token}");
        await client.GetAsync($"/s/{token}");

        var link = LinkByToken(factory, token);
        Assert.Equal(2, link.OpenedCount);
        Assert.NotNull(link.LastOpenedAt);
    }

    [Fact]
    public async Task Parallel_visits_are_all_counted()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var token = AddLink(factory, "statya");

        var client = factory.CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync($"/s/{token}")));

        Assert.Equal(20, LinkByToken(factory, token).OpenedCount);
    }

    [Fact]
    public async Task Guest_gets_the_picture_of_the_shared_article()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "![[shema.png]]");
        WriteAttachment("statya", "shema.png", [1, 2, 3]);
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var token = AddLink(factory, "statya");

        var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/s/{token}");
        var picture = await client.GetAsync($"/s/{token}/shema.png");

        Assert.Contains($"src=\"/s/{token}/shema.png\"", html);
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal([1, 2, 3], await picture.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Link_of_one_article_does_not_open_files_of_another()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        WriteArticle("chuzhaya", "Чужой текст.");
        WriteAttachment("chuzhaya", "tayna.png", [9]);
        RegisterArticle(factory, "chuzhaya", "Чужая статья");
        var token = AddLink(factory, "statya");
        var owner = await LoginClient(factory);

        // Имя без "..": клиент такой адрес не правит, и до сервера доходит именно он.
        var byGuest = await factory.CreateClient().GetAsync($"/s/{token}/tayna.png");
        var byOwner = await owner.GetAsync("/chuzhaya/tayna.png");

        Assert.Equal(HttpStatusCode.NotFound, byGuest.StatusCode);
        // Файл есть и отдаётся по своему адресу — гостю его закрыл именно slug из ссылки.
        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
    }

    [Fact]
    public async Task Folder_link_inside_the_article_does_not_give_a_foreign_file()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var secret = Path.Combine(dataRoot, "secretdir");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "tayna.txt"), "секрет");
        Directory.CreateSymbolicLink(Path.Combine(dataRoot, "articles", "statya", "d"), secret);
        var token = AddLink(factory, "statya");
        var owner = await LoginClient(factory);

        // Гостю такой адрес не даёт даже маршрута, поэтому проверяем не код, а само содержимое.
        var guest = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var byOwner = await owner.GetAsync("/statya/d/tayna.txt");
        var byGuest = await guest.GetAsync($"/s/{token}/d/tayna.txt");

        Assert.DoesNotContain("секрет", await byGuest.Content.ReadAsStringAsync());
        Assert.NotEqual(HttpStatusCode.OK, byGuest.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byOwner.StatusCode);
    }

    [Fact]
    public async Task Nobody_downloads_the_markdown_source()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "---\ntitle: Про ежей\n---\n\nТекст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var token = AddLink(factory, "statya");
        var owner = await LoginClient(factory);

        var byOwner = await owner.GetAsync("/statya/index.md");
        var byGuest = await factory.CreateClient().GetAsync($"/s/{token}/index.md");

        Assert.Equal(HttpStatusCode.NotFound, byOwner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byGuest.StatusCode);
    }

    [Fact]
    public async Task Opening_a_picture_does_not_count_as_a_visit()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "![[shema.png]]");
        WriteAttachment("statya", "shema.png", [1]);
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var token = AddLink(factory, "statya");

        var client = factory.CreateClient();
        await client.GetAsync($"/s/{token}");
        await client.GetAsync($"/s/{token}/shema.png");

        Assert.Equal(1, LinkByToken(factory, token).OpenedCount);
    }

    [Fact]
    public async Task Dead_link_does_not_give_pictures()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        WriteAttachment("statya", "shema.png", [1]);
        RegisterArticle(factory, "statya", "Про ежей");
        var token = AddLink(factory, "statya", revokedAt: DateTimeOffset.UtcNow);

        var response = await factory.CreateClient().GetAsync($"/s/{token}/shema.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Owner_creates_a_link_from_the_article_page()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        var page = await client.GetStringAsync("/statya");
        var antiforgery = AntiforgeryToken(page);
        var response = await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [antiforgery.Name] = antiforgery.Value,
            ["slug"] = "statya",
            ["days"] = "7",
            ["note"] = "Пете",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var link = db.ShareLinks.Single();
        Assert.Equal("statya", link.Slug);
        Assert.Equal("Пете", link.Note);
        Assert.NotNull(link.ExpiresAt);
        Assert.InRange(link.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddDays(6), DateTimeOffset.UtcNow.AddDays(8));
        // Обратно на статью: ссылка видна прямо там, в блоке «Поделиться».
        Assert.Equal("/statya", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Link_without_a_term_has_no_expiry()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        var antiforgery = AntiforgeryToken(await client.GetStringAsync("/statya"));
        await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [antiforgery.Name] = antiforgery.Value,
            ["slug"] = "statya",
            ["days"] = "0",
        }));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Null(db.ShareLinks.Single().ExpiresAt);
    }

    [Fact]
    public async Task Strange_term_and_unknown_article_are_refused()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);
        var antiforgery = AntiforgeryToken(await client.GetStringAsync("/statya"));

        var strangeTerm = await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [antiforgery.Name] = antiforgery.Value, ["slug"] = "statya", ["days"] = "3",
        }));
        var unknownArticle = await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [antiforgery.Name] = antiforgery.Value, ["slug"] = "net-takoy", ["days"] = "7",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, strangeTerm.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownArticle.StatusCode);
    }

    [Fact]
    public async Task Share_without_a_slug_is_refused()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);
        var antiforgery = AntiforgeryToken(await client.GetStringAsync("/statya"));

        var response = await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [antiforgery.Name] = antiforgery.Value, ["slug"] = "", ["days"] = "7",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_cannot_create_a_link()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");

        // Без автоперехода: иначе клиент сам сходит на страницу входа и тест увидит её 200.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["slug"] = "statya", ["days"] = "7",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location!.AbsolutePath);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.ShareLinks);
    }

    [Fact]
    public async Task Post_without_antiforgery_token_is_refused()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        var response = await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["slug"] = "statya", ["days"] = "7",
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("s")]
    [InlineData("S")]
    [InlineData("login")]
    [InlineData("settings")]
    [InlineData("assets")]
    [InlineData("background")]
    public async Task Article_with_the_reserved_slug_is_refused(string slug)
    {
        using var factory = StartFactory();
        var client = StartApiClient(factory);

        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("---\ntitle: Про ежей\n---\nТекст."u8.ToArray()), "index.md", "index.md" },
        };
        var response = await client.PutAsync($"/api/articles/{slug}", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("служебным маршрутом", body);
        Assert.Contains(slug, body);
    }

    [Fact]
    public async Task Settings_page_shows_the_link_with_its_address()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var token = AddLink(factory, "statya");
        var client = await LoginClient(factory);

        var html = await client.GetStringAsync("/settings");

        Assert.Contains($"/s/{token}", html);
        Assert.Contains("Про ежей", html);
    }

    [Fact]
    public async Task Owner_revokes_a_link_and_the_guest_loses_access()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var token = AddLink(factory, "statya");
        var client = await LoginClient(factory);

        var id = LinkByToken(factory, token).Id;
        var antiforgery = AntiforgeryToken(await client.GetStringAsync("/settings"));
        var revoke = await client.PostAsync($"/settings/links/{id}/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [antiforgery.Name] = antiforgery.Value,
            }));

        var guest = await factory.CreateClient().GetAsync($"/s/{token}");

        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Gone, guest.StatusCode);
        Assert.NotNull(LinkByToken(factory, token).RevokedAt);
    }

    [Fact]
    public async Task Second_revoke_answers_404()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var token = AddLink(factory, "statya", revokedAt: DateTimeOffset.UtcNow);
        var client = await LoginClient(factory);

        var id = LinkByToken(factory, token).Id;
        var antiforgery = AntiforgeryToken(await client.GetStringAsync("/settings"));
        var response = await client.PostAsync($"/settings/links/{id}/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                [antiforgery.Name] = antiforgery.Value,
            }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Owner_and_guest_get_different_pages_of_one_article()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "![[shema.png]]");
        WriteAttachment("statya", "shema.png", [1]);
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var first = AddLink(factory, "statya");
        var second = AddLink(factory, "statya");
        var owner = await LoginClient(factory);

        var ownerPage = await owner.GetStringAsync("/statya");
        var firstPage = await factory.CreateClient().GetStringAsync($"/s/{first}");
        var secondPage = await factory.CreateClient().GetStringAsync($"/s/{second}");

        Assert.Contains("user-menu", ownerPage);
        Assert.Contains("src=\"/statya/shema.png\"", ownerPage);
        Assert.DoesNotContain("user-menu", firstPage);
        Assert.Contains($"src=\"/s/{first}/shema.png\"", firstPage);
        Assert.Contains($"src=\"/s/{second}/shema.png\"", secondPage);
        Assert.DoesNotContain("__SHARE_BASE__", firstPage);
        Assert.DoesNotContain("__SHARE_PANEL__", firstPage);
    }

    [Fact]
    public async Task Second_share_changes_the_term_instead_of_making_a_new_link()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        await PostShare(client, "statya", "7");
        var first = SingleLink(factory);

        await PostShare(client, "statya", "30");
        var second = SingleLink(factory);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Token, second.Token);
        Assert.True(second.ExpiresAt > first.ExpiresAt);
    }

    [Fact]
    public async Task A_revoked_link_does_not_come_back_to_life()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        await PostShare(client, "statya", "7");
        var first = SingleLink(factory);

        await PostRevoke(client, "statya");
        await PostShare(client, "statya", "7");

        var links = AllLinks(factory);
        Assert.Equal(2, links.Count);
        Assert.NotEqual(first.Token, links.Single(link => link.RevokedAt is null).Token);

        var guest = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Gone, (await guest.GetAsync($"/s/{first.Token}")).StatusCode);
    }

    [Fact]
    public async Task An_expired_link_is_replaced_by_a_new_one()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        await PostShare(client, "statya", "1");
        var first = SingleLink(factory);
        Expire(factory, first.Id);

        await PostShare(client, "statya", "7");

        var links = AllLinks(factory);
        Assert.Equal(2, links.Count);
        Assert.NotEqual(first.Token, links.Single(link => link.ExpiresAt > DateTimeOffset.UtcNow).Token);
    }

    [Fact]
    public async Task Article_page_shows_the_live_link_and_no_create_button()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        await PostShare(client, "statya", "7");
        var token = SingleLink(factory).Token;

        var html = await client.GetStringAsync("/statya");

        Assert.Contains($"/s/{token}", html);
        Assert.Contains("/share/revoke", html);
        Assert.DoesNotContain("Создать ссылку", html);
    }

    [Fact]
    public async Task A_new_link_shows_up_on_the_cached_page()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        var before = await client.GetStringAsync("/statya");
        Assert.Contains("Создать ссылку", before);

        await PostShare(client, "statya", "7");
        var after = await client.GetStringAsync("/statya");

        Assert.Contains($"/s/{SingleLink(factory).Token}", after);
    }

    [Fact]
    public async Task Revoke_from_the_article_page_closes_the_link()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        var client = await LoginClient(factory);

        await PostShare(client, "statya", "7");
        var token = SingleLink(factory).Token;

        var answer = await PostRevoke(client, "statya");

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        Assert.Equal("/statya", answer.Headers.Location?.ToString());

        var guest = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Gone, (await guest.GetAsync($"/s/{token}")).StatusCode);
    }

    [Fact]
    public async Task Guest_link_works_even_when_the_article_is_closed()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей", ArticleVisibility.Shared);
        var token = AddLink(factory, "statya");
        var owner = await LoginClient(factory);

        var open = await factory.CreateClient().GetAsync($"/s/{token}");
        await PostForm(owner, "/visibility", new() { ["slug"] = "statya", ["visibility"] = "private" });
        var closed = await factory.CreateClient().GetAsync($"/s/{token}");

        // Видимость закрывает статью для заведённых людей, а не для того, кому отдали ссылку.
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    [Fact]
    public async Task Reader_shares_an_open_article_but_not_a_closed_one()
    {
        using var factory = StartFactory();
        WriteArticle("statya", "Текст статьи.");
        RegisterArticle(factory, "statya", "Про ежей");
        WriteArticle("otkrytaya", "Текст статьи.");
        RegisterArticle(factory, "otkrytaya", "Открытая", ArticleVisibility.Shared);
        var client = await LoginClient(factory, UserRole.Reader);

        var closed = await PostShare(client, "statya", "7");
        var open = await PostShare(client, "otkrytaya", "7");

        Assert.Equal(HttpStatusCode.Forbidden, closed.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, open.StatusCode);
        Assert.Equal("otkrytaya", SingleLink(factory).Slug);
    }

}
