using System.Text.RegularExpressions;
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

    /// Отдаёт разметку внутри блока, который открывается тегом openTag, считая вложенные блоки.
    static string SliceDiv(string html, string openTag)
    {
        var start = html.IndexOf(openTag, StringComparison.Ordinal);
        Assert.True(start >= 0, $"в разметке нет {openTag}");

        var depth = 0;
        var at = start;
        while (true)
        {
            var open = html.IndexOf("<div", at, StringComparison.Ordinal);
            var close = html.IndexOf("</div>", at, StringComparison.Ordinal);
            Assert.True(close >= 0, $"незакрытый {openTag}");
            if (open >= 0 && open < close)
            {
                depth++;
                at = open + "<div".Length;
                continue;
            }

            depth--;
            if (depth == 0) return html[(start + openTag.Length)..close];
            at = close + "</div>".Length;
        }
    }

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
        // Дерево и статья лежат внутри панели, а не просто перед футером: берём содержимое
        // панели до парного </div>, поэтому вынос дерева из обёртки тест уронит.
        var shell = SliceDiv(html, "<div class=\"shell\">");
        Assert.Contains("nav-tree", shell);
        Assert.Contains("<main>", shell);
    }

    [Fact]
    public async Task The_login_page_gets_the_same_panel_without_the_tree()
    {
        var html = await StartFactory().CreateClient().GetStringAsync("/login");

        // Два отдельных условия: порядок классов в атрибуте ни на что не влияет.
        Assert.Contains("class=\"shell", html);
        Assert.Contains("shell-plain", html);
        Assert.DoesNotContain("nav-tree", html);
    }

    [Fact]
    public async Task The_stylesheet_answers_all_three_color_scheme_settings()
    {
        var css = await StartFactory().CreateClient().GetStringAsync("/assets/style.css");

        // «Как в системе» — по настройке системы, но выбор «светлая» её перебивает.
        Assert.Contains("prefers-color-scheme: dark", css);
        Assert.Contains(":root:not([data-color-scheme=\"light\"])", css);
        Assert.Contains(":root[data-color-scheme=\"dark\"]", css);
    }

    /// Отдаёт объявления правила: всё между первой { после селектора и следующей }, пробелы сжаты.
    static string Declarations(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.True(start >= 0, $"в стилях нет {selector}");

        var open = css.IndexOf('{', start + selector.Length);
        var close = css.IndexOf('}', open + 1);
        Assert.True(open >= 0 && close > open, $"у {selector} нет тела");

        return Regex.Replace(css[(open + 1)..close], @"\s+", " ").Trim();
    }

    [Fact]
    public async Task Both_dark_palette_blocks_hold_the_same_values()
    {
        var css = await StartFactory().CreateClient().GetStringAsync("/assets/style.css");

        var bySystem = Declarations(css, ":root:not([data-color-scheme=\"light\"])");
        var byChoice = Declarations(css, ":root[data-color-scheme=\"dark\"]");

        // Набор повторён дважды: обычный CSS не умеет отдать один блок двум условиям. Разойдутся
        // блоки — «тёмная» и «как в системе» начнут выглядеть по-разному, и заметить это нечем.
        Assert.Contains("--muted", bySystem);
        Assert.Equal(bySystem, byChoice);
    }

    [Fact]
    public async Task The_stylesheet_puts_the_picture_behind_an_opaque_panel()
    {
        var css = await StartFactory().CreateClient().GetStringAsync("/assets/style.css");

        Assert.Contains("--bg-image", css);
        // Панель непрозрачна: читать длинный текст сквозь снимок тяжело.
        Assert.Contains("background: var(--panel-solid)", css);
        // Размытие осталось только у меню владельца, оно висит прямо над снимком.
        Assert.Contains("backdrop-filter", css);
        Assert.Contains("@supports not", css);
    }
}
