using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

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

        Assert.Contains("Упоминается в", html);
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

        Assert.DoesNotContain("Упоминается в", html);
    }

    [Fact]
    public async Task New_mention_takes_the_target_out_of_the_cache()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nтекст")).EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        Assert.DoesNotContain("Упоминается в", await client.GetStringAsync("/proxmox"));

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

        Assert.DoesNotContain("Упоминается в", await client.GetStringAsync("/proxmox"));
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

        Assert.DoesNotContain("Упоминается в", html);
    }
}
