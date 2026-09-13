using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ThemeRenderTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public ThemeRenderTests(DatabaseFixture database)
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

    /// Заводит владельца и входит: сайт закрыт с Task 13.
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

    // Маршрут статьи требует и файл на диске, и строку в базе (см. PageEndpointsTests) —
    // заводим оба, как это делает публикация через API.
    async Task<string> GetArticleHtml(string markdown)
    {
        var folder = Path.Combine(dataRoot, "articles", "proba");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "index.md"), markdown);

        var factory = StartFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow
            {
                Slug = "proba", Title = "Проба", ContentHash = "x", UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        return await LoginClient(factory).GetStringAsync("/proba");
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    [Fact]
    public async Task Article_page_has_title_meta_and_styles()
    {
        var html = await GetArticleHtml("---\ntitle: Привет\ndescription: Кратко\n---\n# Привет\n");

        Assert.Contains("<title>Привет</title>", html);
        Assert.Contains("<meta name=\"description\" content=\"Кратко\">", html);
        Assert.Contains("/assets/style.css", html);
        Assert.Contains("viewport", html);
    }

    [Fact]
    public async Task Style_sheet_is_served()
    {
        var response = await LoginClient(StartFactory()).GetAsync("/assets/style.css");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Dark_scheme_is_declared()
    {
        var css = await LoginClient(StartFactory()).GetStringAsync("/assets/style.css");

        Assert.Contains("prefers-color-scheme: dark", css);
    }

    [Fact]
    public async Task Callout_and_code_have_styles()
    {
        var css = await LoginClient(StartFactory()).GetStringAsync("/assets/style.css");

        Assert.Contains(".callout", css);
        Assert.Contains("pre", css);
    }

    [Fact]
    public async Task The_page_with_navigation_puts_the_tree_and_the_article_in_one_panel()
    {
        var factory = StartFactory();
        var client = LoginClient(factory);

        var html = await client.GetStringAsync("/");

        Assert.Contains("class=\"shell\"", html);
        Assert.DoesNotContain("shell-plain", html);
        // Дерево и статья должны быть в одной обёртке, а не просто где-то на странице:
        // .shell в разметке ровно один, всё между его открытием и футером — внутри него.
        var shellStart = html.IndexOf("<div class=\"shell\">", StringComparison.Ordinal);
        var shellEnd = html.IndexOf("<footer", shellStart, StringComparison.Ordinal);
        Assert.True(shellStart >= 0 && shellEnd > shellStart);
        var shell = html[shellStart..shellEnd];
        Assert.Contains("nav-tree", shell);
        Assert.Contains("<main>", shell);
    }

    [Fact]
    public async Task The_login_page_gets_the_same_panel_without_the_tree()
    {
        var html = await StartFactory().CreateClient().GetStringAsync("/login");

        Assert.Contains("<div class=\"shell shell-plain\">", html);
        Assert.DoesNotContain("nav-tree", html);
    }
}
