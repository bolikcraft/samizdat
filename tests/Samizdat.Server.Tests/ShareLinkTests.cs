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

    async Task<HttpClient> LoginClient(WebApplicationFactory<Program> factory)
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

    void RegisterArticle(WebApplicationFactory<Program> factory, string slug, string title)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTimeOffset.UtcNow,
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
        RegisterArticle(factory, "statya", "Про ежей");
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
        RegisterArticle(factory, "statya", "Про ежей");
        RegisterArticle(factory, "chuzhaya", "Чужая статья");
        var token = AddLink(factory, "statya");

        var html = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.DoesNotContain("nav-tree", html);
        Assert.DoesNotContain("user-menu", html);
        Assert.DoesNotContain("chuzhaya", html);
        Assert.Contains("noindex", html);
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
        RegisterArticle(factory, "statya", "Про ежей");
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
        RegisterArticle(factory, "statya", "Про ежей");
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
        RegisterArticle(factory, "statya", "Про ежей");
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

        var response = await factory.CreateClient().GetAsync($"/s/{token}/../chuzhaya/tayna.png");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
        RegisterArticle(factory, "statya", "Про ежей");
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
        Assert.Equal($"/settings#link-{link.Id}", response.Headers.Location!.OriginalString);
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

        var client = factory.CreateClient();
        var response = await client.PostAsync("/share", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["slug"] = "statya", ["days"] = "7",
        }));

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
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
        Assert.Contains("/s/", await response.Content.ReadAsStringAsync());
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
        RegisterArticle(factory, "statya", "Про ежей");
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
    }
}
