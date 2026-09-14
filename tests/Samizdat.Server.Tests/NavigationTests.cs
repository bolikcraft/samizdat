using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class NavigationTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public NavigationTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });
    }

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

    void Register(WebApplicationFactory<Program> factory, string slug, string title, string folder = "")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, Folder = folder, ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    static MultipartFormDataContent Article(string markdown)
        => new() { { new ByteArrayContent(Encoding.UTF8.GetBytes(markdown)), "index.md", "index.md" } };

    [Fact]
    public async Task Tree_shows_every_article_title_on_the_index_page()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        WriteArticle("b", "---\ntitle: Bravo\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        Register(factory, "b", "Bravo");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("id=\"nav-tree\"", html);
        Assert.Contains("Alpha", html);
        Assert.Contains("Bravo", html);
    }

    [Fact]
    public async Task Tree_shows_every_article_title_on_an_article_page()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        WriteArticle("b", "---\ntitle: Bravo\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        Register(factory, "b", "Bravo");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/a");

        Assert.Contains("id=\"nav-tree\"", html);
        Assert.Contains("Alpha", html);
        Assert.Contains("Bravo", html);
    }

    [Fact]
    public async Task Tree_shows_the_folder_from_the_article_row()
    {
        WriteArticle("immich", "---\ntitle: Immich\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "immich", "Immich", folder: "PROXMOX");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/immich");

        Assert.Contains("PROXMOX", html);
    }

    [Fact]
    public async Task Current_article_is_marked_and_others_are_not()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        WriteArticle("b", "---\ntitle: Bravo\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        Register(factory, "b", "Bravo");
        var client = LoginClient(factory);

        var onA = await client.GetStringAsync("/a");
        var onB = await client.GetStringAsync("/b");

        Assert.Contains("href=\"/a\" aria-current=\"page\"", onA);
        Assert.DoesNotContain("href=\"/b\" aria-current=\"page\"", onA);
        Assert.Contains("href=\"/b\" aria-current=\"page\"", onB);
        Assert.DoesNotContain("href=\"/a\" aria-current=\"page\"", onB);
    }

    [Fact]
    public async Task New_article_appears_in_the_menu_of_an_already_rendered_page()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        var pages = LoginClient(factory);
        var api = StartApiClient(factory);

        var before = await pages.GetStringAsync("/a");
        Assert.DoesNotContain("Nova", before);

        await api.PutAsync("/api/articles/c", Article("---\ntitle: Nova\n---\nтекст\n"));
        var after = await pages.GetStringAsync("/a");

        Assert.Contains("Nova", after);
    }

    [Fact]
    public async Task Deleted_article_disappears_from_the_menu()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        WriteArticle("b", "---\ntitle: Bravo\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        Register(factory, "b", "Bravo");
        var pages = LoginClient(factory);
        var api = StartApiClient(factory);

        var before = await pages.GetStringAsync("/a");
        Assert.Contains("Bravo", before);

        await api.DeleteAsync("/api/articles/b");
        var after = await pages.GetStringAsync("/a");

        Assert.DoesNotContain("Bravo", after);
    }

    [Fact]
    public async Task Not_found_page_still_shows_the_tree()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        var client = LoginClient(factory);

        var response = await client.GetAsync("/нет-такой");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("id=\"nav-tree\"", html);
        Assert.Contains("Alpha", html);
    }

    [Fact]
    public async Task Login_page_has_no_menu_for_the_anonymous_visitor()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        var client = factory.CreateClient();

        var html = await client.GetStringAsync("/login");

        Assert.DoesNotContain("id=\"nav-tree\"", html);
        Assert.DoesNotContain("Alpha", html);
    }

    [Fact]
    public async Task Sidebar_is_present_on_the_index_and_article_pages()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        var client = LoginClient(factory);

        var index = await client.GetStringAsync("/");
        var article = await client.GetStringAsync("/a");

        Assert.Contains("<aside class=\"sidebar\"", index);
        Assert.Contains("<aside class=\"sidebar\"", article);
    }

    [Fact]
    public async Task Folder_is_rendered_as_a_details_element()
    {
        WriteArticle("immich", "---\ntitle: Immich\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "immich", "Immich", folder: "PROXMOX");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/immich");

        Assert.Contains("<details class=\"nav-folder\" data-path=\"PROXMOX\"", html);
    }

    [Fact]
    public async Task Folder_holding_the_current_article_is_open()
    {
        WriteArticle("immich", "---\ntitle: Immich\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "immich", "Immich", folder: "PROXMOX");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/immich");

        Assert.Contains("data-path=\"PROXMOX\" open>", html);
    }

    [Fact]
    public async Task Folder_without_the_current_article_stays_closed()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        WriteArticle("immich", "---\ntitle: Immich\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        Register(factory, "immich", "Immich", folder: "PROXMOX");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/a");

        Assert.Contains("data-path=\"PROXMOX\">", html);
        Assert.DoesNotContain("data-path=\"PROXMOX\" open>", html);
    }

    [Fact]
    public async Task Sidebar_has_a_search_filter_field()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("type=\"search\"", html);
    }

    [Fact]
    public async Task Sidebar_script_is_linked_on_the_page()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("/assets/sidebar.js", html);
    }

    [Fact]
    public async Task Sidebar_script_file_is_served()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "Alpha");
        var client = LoginClient(factory);

        var response = await client.GetAsync("/assets/sidebar.js");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Article_title_is_escaped_in_the_menu()
    {
        WriteArticle("a", "---\ntitle: Alpha\n---\nтекст\n");
        var factory = StartFactory();
        Register(factory, "a", "<script>alert(1)</script>");
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
