using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Data;
using Samizdat.Server.Search;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class IndexTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    [Fact]
    public void Search_vector_sees_the_word_in_another_form()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow
        {
            Slug = "server", Title = "Мой сервер", ContentHash = "x",
            SearchText = "В доме стоит тихий сервер.", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var found = db.Database
            .SqlQueryRaw<bool>("""SELECT "SearchVector" @@ websearch_to_tsquery('russian', 'серверы') AS "Value" FROM articles""")
            .Single();

        Assert.True(found);
    }

    [Fact]
    public void Meta_vector_holds_the_title_but_not_the_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow
        {
            Slug = "tayna", Title = "Тайный сервер", ContentHash = "x",
            SearchText = "гипервизор внутри", UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();

        var byTitle = db.Database
            .SqlQueryRaw<bool>("""SELECT "MetaVector" @@ websearch_to_tsquery('russian', 'серверы') AS "Value" FROM articles""")
            .Single();
        var byText = db.Database
            .SqlQueryRaw<bool>("""SELECT "MetaVector" @@ websearch_to_tsquery('russian', 'гипервизор') AS "Value" FROM articles""")
            .Single();

        Assert.True(byTitle);
        Assert.False(byText);
    }

    [Fact]
    public void Links_of_a_deleted_article_go_with_it()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow { Slug = "odna", Title = "Одна", ContentHash = "x" });
        db.SaveChanges();
        db.ArticleLinks.Add(new ArticleLinkRow { FromSlug = "odna", ToSlug = "drugaya" });
        db.SaveChanges();

        db.Articles.Remove(db.Articles.Single(row => row.Slug == "odna"));
        db.SaveChanges();

        Assert.Empty(db.ArticleLinks.ToList());
    }

    [Fact]
    public void Link_to_an_article_that_is_not_published_yet_is_allowed()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        db.Articles.Add(new ArticleRow { Slug = "odna", Title = "Одна", ContentHash = "x" });
        db.SaveChanges();
        db.ArticleLinks.Add(new ArticleLinkRow { FromSlug = "odna", ToSlug = "eshche-net" });
        db.SaveChanges();

        Assert.Single(db.ArticleLinks.ToList());
    }

    [Fact]
    public async Task Too_long_description_is_refused_with_a_clear_answer()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        // 1 МБ — предел tsvector на документ; такое описание валило выкладку пятисоткой.
        var description = string.Join(' ', Enumerable.Range(0, 150_000).Select(number => $"slovo{number}"));

        var answer = await TestPublisher.Push(
            client, "dlinnoe", $"---\ntitle: Длинное\ndescription: {description}\n---\nтекст\n");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Contains("слишком длинное", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refused_republish_leaves_the_file_on_disk_untouched()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);
        var original = "---\ntitle: Длинное\n---\nобычный текст\n";

        (await TestPublisher.Push(client, "dlinnoe", original)).EnsureSuccessStatusCode();

        var description = string.Join(' ', Enumerable.Range(0, 150_000).Select(number => $"slovo{number}"));
        var answer = await TestPublisher.Push(
            client, "dlinnoe", $"---\ntitle: Длинное\ndescription: {description}\n---\nновый текст\n");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        var onDisk = await File.ReadAllTextAsync(Path.Combine(dataRoot, "articles", "dlinnoe", "index.md"));
        Assert.Equal(original, onDisk);
    }

    [Fact]
    public async Task Refused_publish_of_a_new_article_leaves_no_row_and_no_folder()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        var description = string.Join(' ', Enumerable.Range(0, 150_000).Select(number => $"slovo{number}"));
        var answer = await TestPublisher.Push(
            client, "novoe", $"---\ntitle: Новое\ndescription: {description}\n---\nтекст\n");

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.Articles.Where(row => row.Slug == "novoe"));
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", "novoe")));
    }

    [Fact]
    public async Task Refused_republish_keeps_the_previous_attachments()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        using (var form = TestPublisher.Form("---\ntitle: Со схемой\n---\nтекст\n", ("shema.png", [1, 2, 3])))
        {
            form.Add(new StringContent(""), "folder");
            (await client.PutAsync("/api/articles/so-shemoy", form)).EnsureSuccessStatusCode();
        }

        var description = string.Join(' ', Enumerable.Range(0, 150_000).Select(number => $"slovo{number}"));
        using (var form = TestPublisher.Form(
                   $"---\ntitle: Со схемой\ndescription: {description}\n---\nтекст\n", ("drugoe.png", [4])))
        {
            form.Add(new StringContent(""), "folder");
            var answer = await client.PutAsync("/api/articles/so-shemoy", form);
            Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        }

        var folder = Path.Combine(dataRoot, "articles", "so-shemoy");
        Assert.True(File.Exists(Path.Combine(folder, "shema.png")));
        Assert.False(File.Exists(Path.Combine(folder, "drugoe.png")));
    }

    [Fact]
    public async Task Deleting_an_article_without_a_file_on_disk_still_succeeds()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow { Slug = "propavshaya", Title = "Пропавшая", ContentHash = "h1" });
            db.SaveChanges();
        }

        var response = await client.DeleteAsync("/api/articles/propavshaya");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Successful_publish_leaves_no_staging_or_backup_folders()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        (await TestPublisher.Push(client, "obychnaya", "---\ntitle: Обычная\n---\nv1\n")).EnsureSuccessStatusCode();
        (await TestPublisher.Push(client, "obychnaya", "---\ntitle: Обычная\n---\nv2\n")).EnsureSuccessStatusCode();

        var leftovers = Directory.EnumerateDirectories(Path.Combine(dataRoot, "articles"))
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith(".tmp-") || name.StartsWith(".old-"));

        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task Publishing_fills_the_text_and_the_links()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        (await TestPublisher.Push(client, "pervaya", "---\ntitle: Первая\n---\n\nТекст про [[Вторая заметка]]."))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single();

        Assert.Contains("Текст про", row.SearchText);
        Assert.Equal(row.ContentHash, row.IndexedHash);
        Assert.Equal(["Вторая заметка", "vtoraya-zametka"],
                     db.ArticleLinks.Select(link => link.ToSlug).ToList().Order());
    }

    [Fact]
    public async Task Link_inside_a_callout_fills_both_the_text_and_the_links()
    {
        // ArticleIndexer разбирает markdown один раз и зовёт WikiLinks.Targets перед
        // PlainText.Extract на том же document — этот тест ловит регрессию порядка.
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        var body = "> [!note] Заголовок\n> см. [[Вторая заметка]] и текст\n";

        (await TestPublisher.Push(client, "pervaya", $"---\ntitle: Первая\n---\n\n{body}"))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single();

        Assert.Contains("Заголовок", row.SearchText);
        Assert.Contains("текст", row.SearchText);
        Assert.Equal(["Вторая заметка", "vtoraya-zametka"],
                     db.ArticleLinks.Select(link => link.ToSlug).ToList().Order());
    }

    [Fact]
    public async Task Control_character_from_the_text_does_not_reach_the_index()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        // Такими символами размечается подсветка цитаты: из текста заметки они подделали бы её.
        var body = $"тут {SearchSnippet.Start}сервер{SearchSnippet.Stop} стоит";

        (await TestPublisher.Push(client, "statya", $"---\ntitle: Статья\n---\n\n{body}"))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        var text = db.Articles.Single().SearchText;

        Assert.DoesNotContain(SearchSnippet.Start.ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(SearchSnippet.Stop.ToString(), text, StringComparison.Ordinal);
        Assert.Contains("тут сервер стоит", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_character_from_the_title_does_not_reach_the_index()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        // Заголовок и описание тоже идут в поисковые векторы и оттуда на страницу находок.
        var matter = $"title: Тайный{SearchSnippet.Start}сервер\ndescription: про{SearchSnippet.Stop}дом";

        (await TestPublisher.Push(client, "statya", $"---\n{matter}\n---\n\nтекст")).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single();

        Assert.Equal("Тайныйсервер", row.Title);
        Assert.Equal("продом", row.Description);
    }

    [Fact]
    public async Task Republishing_drops_a_link_that_is_gone()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        (await TestPublisher.Push(client, "pervaya", "---\ntitle: Первая\n---\n\n[[vtoraya]]"))
            .EnsureSuccessStatusCode();
        (await TestPublisher.Push(client, "pervaya", "---\ntitle: Первая\n---\n\nбез ссылок"))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        Assert.Empty(db.ArticleLinks.ToList());
        Assert.Contains("без ссылок", db.Articles.Single().SearchText);
    }

    [Fact]
    public async Task Republishing_keeps_a_link_that_stayed()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        (await TestPublisher.Push(client, "pervaya", "---\ntitle: Первая\n---\n\n[[vtoraya]]"))
            .EnsureSuccessStatusCode();
        (await TestPublisher.Push(client, "pervaya", "---\ntitle: Первая\n---\n\nдругой текст, та же [[vtoraya]]"))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        Assert.Equal("vtoraya", Assert.Single(db.ArticleLinks.ToList()).ToSlug);
    }

    [Fact]
    public async Task Article_does_not_link_to_itself()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        (await TestPublisher.Push(client, "pervaya", "---\ntitle: Первая\n---\n\nсам на себя [[pervaya]]"))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        Assert.Empty(db.ArticleLinks.ToList());
    }

    [Fact]
    public async Task Link_to_itself_in_another_case_is_not_stored()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        (await TestPublisher.Push(client, "proxmox", "---\ntitle: Proxmox\n---\n\nсам на себя [[Proxmox]]"))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        Assert.Empty(db.ArticleLinks.ToList());
    }

    [Fact]
    public async Task Emoji_on_the_trim_border_does_not_break_publishing()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        // Эмодзи ровно на границе обрезки: разрезанная пополам суррогатная пара валила выкладку.
        var head = new string('a', ArticleIndexer.MaxSearchText - 1);
        var answer = await TestPublisher.Push(client, "emodzi", $"---\ntitle: Эмодзи\n---\n\n{head}😀хвост");

        Assert.True(answer.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Huge_article_is_published_with_a_trimmed_index()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        // У tsvector предел 1 МБ на документ: длинная статья должна выложиться, потеряв хвост индекса,
        // а не получить отказ.
        var body = string.Join(' ', Enumerable.Range(0, 200_000).Select(number => $"slovo{number}"));
        var answer = await TestPublisher.Push(client, "dlinnaya", $"---\ntitle: Длинная\n---\n\n{body}");

        Assert.True(answer.IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single();

        Assert.True(row.SearchText.Length <= ArticleIndexer.MaxSearchText);
        Assert.StartsWith("slovo0 ", row.SearchText);
    }

    [Fact]
    public async Task Link_to_a_very_long_name_does_not_break_publishing()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = TestPublisher.ClientWithToken(factory);

        // Заголовок заметки длиннее slug (varchar(200)): такая цель не может совпасть ни с одной
        // статьёй, но раньше валила выкладку ошибкой базы.
        var name = new string('я', 300);
        var answer = await TestPublisher.Push(client, "statya", $"---\ntitle: Статья\n---\n\nсм. [[{name}]]");

        Assert.True(answer.IsSuccessStatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.All(db.ArticleLinks.ToList(), link => Assert.True(link.ToSlug.Length <= 200));
    }

    [Fact]
    public async Task One_broken_article_does_not_stop_the_index_of_the_others()
    {
        database.ResetDatabase();
        await WriteArticleFile("krivaya", $"---\ntitle: Кривая\n---\n\n{ManyWords("slovo", 40_000)}");
        await WriteArticleFile("horoshaya", "---\ntitle: Хорошая\n---\n\nобычный текст");

        using (var first = CreateFactory())
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            // Описание одно влезает в tsvector, а вместе с текстом статьи уже нет: запись такой
            // строки отказом базы не должна оставить соседнюю статью без индекса.
            db.Articles.Add(new ArticleRow
            {
                Slug = "krivaya", Title = "Кривая", ContentHash = "h1",
                Description = ManyWords("opisanie", 40_000),
            });
            db.Articles.Add(new ArticleRow { Slug = "horoshaya", Title = "Хорошая", ContentHash = "h2" });
            db.SaveChanges();
        }

        using var factory = CreateFactory();
        var answer = await factory.CreateClient().GetAsync("/login");
        Assert.True(answer.IsSuccessStatusCode);

        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var good = after.Articles.Single(row => row.Slug == "horoshaya");
        var broken = after.Articles.Single(row => row.Slug == "krivaya");

        Assert.Contains("обычный текст", good.SearchText);
        Assert.Equal(good.ContentHash, good.IndexedHash);
        Assert.Equal("", broken.SearchText);
    }

    Task WriteArticleFile(string slug, string text)
    {
        Directory.CreateDirectory(Path.Combine(dataRoot, "articles", slug));
        return File.WriteAllTextAsync(Path.Combine(dataRoot, "articles", slug, "index.md"), text);
    }

    static string ManyWords(string prefix, int count)
        => string.Join(' ', Enumerable.Range(0, count).Select(number => $"{prefix}{number}"));

    [Fact]
    public async Task Start_builds_the_index_of_an_old_article()
    {
        database.ResetDatabase();
        Directory.CreateDirectory(Path.Combine(dataRoot, "articles", "staraya"));
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "articles", "staraya", "index.md"),
                                     "---\ntitle: Старая\n---\n\nТекст про [[proxmox]].");

        using (var first = CreateFactory())
        using (var scope = first.Services.CreateScope())
        {
            // Строка от прежних этапов: текста и ссылок у неё нет.
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow { Slug = "staraya", Title = "Старая", ContentHash = "h1" });
            db.SaveChanges();
        }

        using var factory = CreateFactory();
        // Хост поднимается лениво: первый запрос запускает и достройку индекса.
        await factory.CreateClient().GetAsync("/login");

        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = after.Articles.Single();

        Assert.Contains("Текст про", row.SearchText);
        Assert.Equal("h1", row.IndexedHash);
        Assert.Contains(after.ArticleLinks.ToList(), link => link.ToSlug == "proxmox");
    }

    [Fact]
    public async Task Start_does_not_touch_an_article_that_is_already_indexed()
    {
        database.ResetDatabase();
        Directory.CreateDirectory(Path.Combine(dataRoot, "articles", "statya"));
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "articles", "statya", "index.md"),
                                     "---\ntitle: Статья\n---\n\nсвежий текст");

        using (var first = CreateFactory())
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow
            {
                Slug = "statya", Title = "Статья", ContentHash = "h1", IndexedHash = "h1",
                SearchText = "старый текст",
            });
            db.SaveChanges();
        }

        using var factory = CreateFactory();
        await factory.CreateClient().GetAsync("/login");

        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<SamizdatDbContext>();

        Assert.Equal("старый текст", after.Articles.Single().SearchText);
    }

    [Fact]
    public async Task Article_without_a_file_does_not_stop_the_start()
    {
        database.ResetDatabase();

        using (var first = CreateFactory())
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow { Slug = "propavshaya", Title = "Пропавшая", ContentHash = "h1" });
            db.SaveChanges();
        }

        using var factory = CreateFactory();
        var answer = await factory.CreateClient().GetAsync("/login");

        Assert.True(answer.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Article_with_a_broken_front_matter_does_not_stop_the_start()
    {
        database.ResetDatabase();
        Directory.CreateDirectory(Path.Combine(dataRoot, "articles", "krivaya"));
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "articles", "krivaya", "index.md"),
                                     "---\ntitle: [не закрыт\n  - кривой отступ\n---\n\nтекст");

        using (var first = CreateFactory())
        using (var scope = first.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow { Slug = "krivaya", Title = "Кривая", ContentHash = "h1" });
            db.SaveChanges();
        }

        using var factory = CreateFactory();
        var answer = await factory.CreateClient().GetAsync("/login");

        Assert.True(answer.IsSuccessStatusCode);
    }
}
