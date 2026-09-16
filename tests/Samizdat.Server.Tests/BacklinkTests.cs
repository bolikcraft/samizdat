using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Search;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class BacklinkTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    [Fact]
    public async Task Article_shows_who_mentions_it()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nстоит [[proxmox]]")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/proxmox");

        Assert.Contains("These articles mention it", html);
        Assert.Contains("href=\"/dom\"", html);
    }

    [Fact]
    public async Task Article_without_mentions_has_no_block()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "odna", "---\ntitle: Одна\n---\n\nтекст")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/odna");

        Assert.DoesNotContain("These articles mention it", html);
    }

    [Fact]
    public async Task New_mention_takes_the_target_out_of_the_cache()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        Assert.DoesNotContain("These articles mention it", await client.GetStringAsync("/proxmox"));

        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nстоит [[proxmox]]")).EnsureSuccessStatusCode();

        Assert.Contains("href=\"/dom\"", await client.GetStringAsync("/proxmox"));
    }

    [Fact]
    public async Task Deleted_article_leaves_no_mention()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nстоит [[proxmox]]")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        Assert.Contains("href=\"/dom\"", await client.GetStringAsync("/proxmox"));

        (await api.DeleteAsync("/api/articles/dom")).EnsureSuccessStatusCode();

        Assert.DoesNotContain("These articles mention it", await client.GetStringAsync("/proxmox"));
    }

    [Fact]
    public async Task Mention_from_a_private_article_is_not_a_link_for_the_reader()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "tayna", "---\ntitle: Тайна\n---\n\nстоит [[proxmox]]")).EnsureSuccessStatusCode();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Single(row => row.Slug == "proxmox").Visibility = ArticleVisibility.Shared;
            db.SaveChanges();
        }

        var client = await TestLogin.AsReader(factory);
        var html = await client.GetStringAsync("/proxmox");

        Assert.Contains("Тайна", html);
        Assert.DoesNotContain("href=\"/tayna\"", html);
    }

    [Fact]
    public async Task Mention_by_the_note_name_counts()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "zametka-pro-proxmox", "---\ntitle: Заметка про Proxmox\n---\n\nтекст"))
            .EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nсм. [[Заметка про Proxmox]]"))
            .EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/zametka-pro-proxmox");

        Assert.Contains("href=\"/dom\"", html);
    }

    [Fact]
    public async Task Guest_page_has_no_mentions()
    {
        // Гостевая страница по ссылке /s/{token}: блока «Упоминается в» там быть не должно —
        // чужие заголовки постороннему не показываем. Собери ссылку по образцу ShareLinkTests.
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nстоит [[proxmox]]")).EnsureSuccessStatusCode();

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var link = new ShareLinkRow
            {
                Token = ShareToken.Create(),
                Slug = "proxmox",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ShareLinks.Add(link);
            db.SaveChanges();
            token = link.Token;
        }

        var html = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.DoesNotContain("These articles mention it", html);
    }

    [Fact]
    public async Task Reindex_takes_the_stale_mention_out_of_the_cache()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nстоит [[proxmox]]")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        Assert.Contains("These articles mention it", await client.GetStringAsync("/proxmox"));

        // Правка мимо PUT: ContentHash в базе не трогаем, поэтому без force reindex её не заметит.
        var file = Path.Combine(dataRoot, "articles", "dom", "index.md");
        File.WriteAllText(file, "---\ntitle: Дом\n---\n\nтекст без ссылки");

        using (var scope = factory.Services.CreateScope())
        {
            var files = scope.ServiceProvider.GetRequiredService<ArticleFiles>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            IndexBackfill.Run(factory.Services, files, logger, force: true);
        }

        Assert.DoesNotContain("These articles mention it", await client.GetStringAsync("/proxmox"));
    }

    [Fact]
    public async Task Source_with_several_forms_of_the_link_appears_once()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "zametka-pro-proxmox", "---\ntitle: Заметка про Proxmox\n---\n\nтекст"))
            .EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom",
            "---\ntitle: Дом\n---\n\nсм. [[proxmox]], [[Proxmox]] и [[Заметка про Proxmox]]"))
            .EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/proxmox");

        var section = BacklinksSection(html);
        Assert.Single(Regex.Matches(section, "Дом"));
    }

    // Пример из ревью: файл «Моя заметка.md», slug взят из title.
    [Fact]
    public async Task Link_by_the_file_name_finds_an_article_with_another_slug()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "kak-nastroit-server", "---\ntitle: Как настроить сервер\n---\n\nтекст",
                                  name: "Моя заметка")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nсм. [[Моя заметка]]", name: "Дом"))
            .EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);

        Assert.Contains("<a href=\"/kak-nastroit-server\">Моя заметка</a>", await client.GetStringAsync("/dom"));
        Assert.Contains("href=\"/dom\"", BacklinksSection(await client.GetStringAsync("/kak-nastroit-server")));
    }

    [Fact]
    public async Task Slug_wins_over_a_file_name_with_the_same_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "drugaya", "---\ntitle: Другая\n---\n\nтекст", name: "proxmox"))
            .EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nстоит [[proxmox]]")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);

        Assert.Contains("<a href=\"/proxmox\">proxmox</a>", await client.GetStringAsync("/dom"));
        Assert.Contains("href=\"/dom\"", BacklinksSection(await client.GetStringAsync("/proxmox")));
        Assert.DoesNotContain("These articles mention it", await client.GetStringAsync("/drugaya"));
    }

    [Fact]
    public async Task Source_linking_by_slug_and_by_file_name_appears_once()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "kak-nastroit-server", "---\ntitle: Как настроить сервер\n---\n\nтекст",
                                  name: "Моя заметка")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(api, "dom",
            "---\ntitle: Дом\n---\n\nсм. [[kak-nastroit-server]] и [[Моя заметка]]")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var section = BacklinksSection(await client.GetStringAsync("/kak-nastroit-server"));

        Assert.Single(Regex.Matches(section, "Дом"));
    }

    // Заголовок статьи-источника есть ещё и в дереве навигации — искать нужно строго в блоке.
    static string BacklinksSection(string html)
    {
        var start = html.IndexOf("<section class=\"backlinks\">", StringComparison.Ordinal);
        Assert.True(start >= 0, "Блока «Упоминается в» нет на странице");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }
}
