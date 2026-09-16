using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Core;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class PublishApiTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    (WebApplicationFactory<Program> Factory, HttpClient Client) StartWithToken()
    {
        var (factory, client, _, _) = StartWithTokenAndOwner();
        return (factory, client);
    }

    (WebApplicationFactory<Program> Factory, HttpClient Client, string Login, string Password) StartWithTokenAndOwner()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

        var (client, login, password) = TestPublisher.ClientWithOwner(factory);
        return (factory, client, login, password);
    }

    // Страницы сайта открывает cookie-сессия, а не Bearer-токен: логинимся отдельным клиентом.
    static async Task<HttpClient> LoginPageClient(WebApplicationFactory<Program> factory, string login, string password)
    {
        var client = factory.CreateClient();
        await TestLogin.PostLogin(client, login, password);
        return client;
    }

    static MultipartFormDataContent Article(string markdown, params (string Name, byte[] Bytes)[] files) =>
        TestPublisher.Form(markdown, files);

    [Fact]
    public async Task Put_writes_file_and_row()
    {
        var (factory, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/privet",
            Article("---\ntitle: Привет\n---\nтекст\n"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(dataRoot, "articles", "privet", "index.md")));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single(article => article.Slug == "privet");
        Assert.Equal("Привет", row.Title);
        Assert.NotEmpty(row.ContentHash);
    }

    // Статьи в этом файле делят строку по slug в общей базе (коллекция "db"): свой slug на тест,
    // иначе проверка "строки нет" ловит чужую строку, оставленную другим тестом.
    static string UniqueSlug() => $"folder-{Guid.NewGuid():N}";

    [Fact]
    public async Task Put_stores_the_folder_field()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        var content = Article("---\ntitle: Привет\n---\nтекст\n");
        content.Add(new StringContent("Заметки/PROXMOX"), "folder");

        var response = await client.PutAsync($"/api/articles/{slug}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Equal("Заметки/PROXMOX", db.Articles.Single(article => article.Slug == slug).Folder);
    }

    [Fact]
    public async Task Put_without_folder_field_stores_empty_folder()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();

        var response = await client.PutAsync($"/api/articles/{slug}", Article("текст"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Equal("", db.Articles.Single(article => article.Slug == slug).Folder);
    }

    [Theory]
    [InlineData("../секрет")]
    [InlineData("/корень")]
    [InlineData("Заметки//пусто")]
    [InlineData("Заметки\\x")]
    public async Task Put_with_bad_folder_is_refused_and_article_is_not_written(string folder)
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        var content = Article("текст");
        content.Add(new StringContent(folder), "folder");

        var response = await client.PutAsync($"/api/articles/{slug}", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", slug)));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.Articles.Where(article => article.Slug == slug));
    }

    [Fact]
    public async Task Put_replaces_previous_version_and_removes_old_attachments()
    {
        var (_, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("v1", ("old.png", [1])));

        await client.PutAsync("/api/articles/st", Article("v2", ("new.png", [2])));

        var folder = Path.Combine(dataRoot, "articles", "st");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(folder, "index.md")));
        Assert.True(File.Exists(Path.Combine(folder, "new.png")));
        Assert.False(File.Exists(Path.Combine(folder, "old.png")));
    }

    // index.md затирал саму статью. Хэш совпадал с хэшем CLI, и следующий push ничего не чинил.
    [Theory]
    [InlineData("index.md")]
    [InlineData("INDEX.MD")]
    [InlineData("a\\b.png")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("bad\u0001.png")]
    public async Task Put_with_a_bad_attachment_name_is_refused_and_the_article_stays(string name)
    {
        var (_, client) = StartWithToken();
        var slug = UniqueSlug();
        (await client.PutAsync($"/api/articles/{slug}", Article("v1"))).EnsureSuccessStatusCode();

        var response = await client.PutAsync($"/api/articles/{slug}", Article("v2", (name, [1, 2, 3])));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var folder = Path.Combine(dataRoot, "articles", slug);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "index.md")));
        Assert.Equal(["index.md"], Directory.EnumerateFiles(folder).Select(file => Path.GetFileName(file)!));
    }

    [Fact]
    public async Task State_returns_slug_to_hash()
    {
        var (_, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("текст"));

        var state = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/state");

        Assert.NotNull(state);
        Assert.True(state!.ContainsKey("st"));
    }

    [Fact]
    public async Task Delete_removes_file_and_row()
    {
        var (factory, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("текст"));

        var response = await client.DeleteAsync("/api/articles/st");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", "st")));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Empty(db.Articles.Where(article => article.Slug == "st"));
    }

    // Без блокировки на слаг второй PUT переносит существующий каталог в .old- в тот момент,
    // когда первый ждёт того же движения: Directory.Move у одного из них не находит то, что
    // ожидал. Блокировка обязана развести их по очереди, а не уронить одного из двух.
    [Fact]
    public async Task Two_concurrent_puts_of_the_same_slug_leave_one_consistent_version()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        await client.PutAsync($"/api/articles/{slug}", Article("---\ntitle: V0\n---\nv0"));

        // Восемь разом, не два: гонка на Directory.Move ловится не при каждой паре, а с запасом
        // конкурентов — почти всегда.
        const int concurrentWriters = 8;
        var answers = await Task.WhenAll(Enumerable.Range(1, concurrentWriters).Select(i =>
            client.PutAsync($"/api/articles/{slug}", Article($"---\ntitle: V{i}\n---\nv{i}"))));

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.OK, answer.StatusCode));

        var articlesRoot = Path.Combine(dataRoot, "articles");
        Assert.DoesNotContain(Directory.EnumerateDirectories(articlesRoot),
            dir => Path.GetFileName(dir)!.StartsWith('.'));

        var content = await File.ReadAllTextAsync(Path.Combine(articlesRoot, slug, "index.md"));
        var winner = Enumerable.Range(1, concurrentWriters).Single(i => content.EndsWith($"v{i}"));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single(article => article.Slug == slug);
        Assert.Equal($"V{winner}", row.Title);
    }

    // Число повторяет SlugLockNamespace в ApiEndpoints — своё пространство ключей
    // pg_advisory_xact_lock, отдельное от QueueLock регистрации.
    const int SlugLockNamespace = 587_240_119;

    // Блокировка обязана быть на слаг, а не одна на всех: занимаем слаг A вручную, отдельным
    // соединением и незакоммиченной транзакцией — будто там уже идёт PUT/DELETE — и смотрим,
    // что слаг B это не задерживает, а слаг A ждёт и проходит только после освобождения.
    // Простой прогон двух PUT (как раньше) зеленеет и без блокировки на слаг вовсе: он меряет
    // только итоговый успех, не очередь.
    [Fact(Timeout = 10_000)]
    public async Task A_slug_locked_elsewhere_does_not_hold_up_a_different_slug()
    {
        var (factory, client) = StartWithToken();
        var slugA = UniqueSlug();
        var slugB = UniqueSlug();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        await using var externalLock = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({SlugLockNamespace}, hashtext({slugA}))");

        // Слаг B занят другим ключом — проходит, пока чужая блокировка ещё держится.
        var bResponse = await client.PutAsync($"/api/articles/{slugB}", Article("b"));
        Assert.Equal(HttpStatusCode.OK, bResponse.StatusCode);

        // Слаг A ждёт: запрос запущен, но не может завершиться, пока блокировка снаружи жива.
        var aTask = client.PutAsync($"/api/articles/{slugA}", Article("a"));
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(aTask.IsCompleted, "PUT слага A прошёл, хотя его слаг ещё занят другим соединением");

        await externalLock.RollbackAsync();
        var aResponse = await aTask;

        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(dataRoot, "articles", slugA, "index.md")));
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(dataRoot, "articles", slugB, "index.md")));
    }

    [Fact]
    public async Task Delete_during_a_put_of_the_same_slug_leaves_no_mess()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        await client.PutAsync($"/api/articles/{slug}", Article("v0"));

        // Несколько PUT разом с одним DELETE: одиночная пара ловит гонку на Directory.Move не
        // всегда, запас конкурентов — почти всегда.
        const int concurrentWriters = 6;
        var writes = Enumerable.Range(1, concurrentWriters)
            .Select(i => client.PutAsync($"/api/articles/{slug}", Article($"v{i}")))
            .Append(client.DeleteAsync($"/api/articles/{slug}"));
        var answers = await Task.WhenAll(writes);

        Assert.All(answers, answer => Assert.Equal(HttpStatusCode.OK, answer.StatusCode));

        var articlesRoot = Path.Combine(dataRoot, "articles");
        Assert.DoesNotContain(Directory.EnumerateDirectories(articlesRoot),
            dir => Path.GetFileName(dir)!.StartsWith('.'));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var rowExists = db.Articles.Any(article => article.Slug == slug);
        var folderExists = Directory.Exists(Path.Combine(articlesRoot, slug));
        // Что бы ни победило по порядку — база и диск обязаны сойтись на одном исходе.
        Assert.Equal(rowExists, folderExists);
    }

    [Fact]
    public async Task Get_markdown_returns_source()
    {
        var (_, client) = StartWithToken();
        await client.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nтело\n"));

        var text = await client.GetStringAsync("/api/articles/st.md");

        Assert.Contains("тело", text);
    }

    [Fact]
    public async Task Broken_front_matter_returns_400_with_file_name()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/st", Article("---\ntitle: [\n---\nx"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("фронтматтер", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Slug_with_slash_is_refused()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/..%2fetc", Article("x"));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        Assert.False(File.Exists(Path.Combine(dataRoot, "..", "etc", "index.md")));
    }

    [Fact]
    public async Task Slug_starting_with_dot_is_refused()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/.hidden", Article("x"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "articles", ".hidden")));
    }

    // %2f/%2F в значении маршрута ASP.NET Core намеренно не декодирует в "/" (иначе слаг мог бы
    // расползтись на сегменты пути), а %3f и %23 декодирует как обычный текст, в "?" и "#". В обоих
    // случаях slug получает буквальный ?, # или % — ссылка на статью раскодирует его снова, уже не туда.
    [Theory]
    [InlineData("a%2fb")]
    [InlineData("a%2Fb")]
    [InlineData("a%3Fb")]
    [InlineData("a%23b")]
    public async Task Slug_with_a_url_reserved_character_is_refused(string rawSlug)
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync($"/api/articles/{rawSlug}", Article("x"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var articles = Path.Combine(dataRoot, "articles");
        Assert.Empty(Directory.Exists(articles) ? Directory.EnumerateFileSystemEntries(articles) : []);
    }

    [Fact]
    public async Task Empty_slug_is_refused()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/", Article("x"));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
                    or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Page_reflects_new_content_after_put()
    {
        var (factory, api, login, password) = StartWithTokenAndOwner();
        var pages = await LoginPageClient(factory, login, password);

        await api.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nверсия раз\n"));
        var first = await pages.GetStringAsync("/st");
        Assert.Contains("версия раз", first);

        await api.PutAsync("/api/articles/st", Article("---\ntitle: T\n---\nверсия два\n"));
        var second = await pages.GetStringAsync("/st");

        Assert.Contains("версия два", second);
        Assert.DoesNotContain("версия раз", second);
    }

    [Fact]
    public async Task Slug_search_is_reserved()
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync("/api/articles/search", Article("---\ntitle: Поиск\n---\n\nтекст"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("a%5Cb")]
    [InlineData("..%5C..%5Cevil")]
    [InlineData("a%01b")]
    public async Task Slug_with_a_backslash_or_a_control_character_is_refused(string rawSlug)
    {
        var (_, client) = StartWithToken();

        var response = await client.PutAsync($"/api/articles/{rawSlug}", Article("x"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var articles = Path.Combine(dataRoot, "articles");
        Assert.Empty(Directory.Exists(articles) ? Directory.EnumerateFileSystemEntries(articles) : []);
    }

    // Раньше длинный slug падал на имени служебного каталога с IOException и ответом 500.
    [Theory]
    [InlineData(201, "a")]
    [InlineData(230, "a")]
    [InlineData(101, "я")]
    public async Task Slug_longer_than_200_bytes_is_refused_with_400(int count, string letter)
    {
        var (_, client) = StartWithToken();
        var slug = string.Concat(Enumerable.Repeat(letter, count));

        var response = await client.PutAsync($"/api/articles/{Uri.EscapeDataString(slug)}", Article("x"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("200", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Slug_of_exactly_200_bytes_is_accepted()
    {
        var (_, client) = StartWithToken();
        var slug = new string('d', 200);

        var response = await client.PutAsync($"/api/articles/{slug}", Article("x"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(dataRoot, "articles", slug, "index.md")));
    }

    [Fact]
    public async Task Non_latin_slug_opens_as_a_page_and_downloads()
    {
        var (factory, api, login, password) = StartWithTokenAndOwner();
        var pages = await LoginPageClient(factory, login, password);
        const string slug = "日本語";

        var put = await api.PutAsync($"/api/articles/{Uri.EscapeDataString(slug)}",
                                     Article("---\ntitle: 日本語\n---\nテキスト\n"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var page = await pages.GetAsync($"/{Uri.EscapeDataString(slug)}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("テキスト", html);
        Assert.Contains($"href=\"/{slug}\"", html);

        var download = await pages.GetAsync($"/download/{Uri.EscapeDataString(slug)}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task Put_stores_the_note_name_and_hashes_it()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        const string markdown = "---\ntitle: Как настроить сервер\n---\nтекст\n";
        var content = Article(markdown);
        content.Add(new StringContent("Моя заметка"), "name");

        var response = await client.PutAsync($"/api/articles/{slug}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single(article => article.Slug == slug);
        Assert.Equal("Моя заметка", row.NoteName);
        Assert.Equal(ArticleHash.Compute(Encoding.UTF8.GetBytes(markdown), [], "", "Моя заметка"), row.ContentHash);
    }

    // Старый CLI и плагин Obsidian поля name не шлют: хэш должен совпасть с их хэшем.
    [Fact]
    public async Task Put_without_name_keeps_the_hash_of_an_old_client()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();

        var response = await client.PutAsync($"/api/articles/{slug}", Article("текст"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single(article => article.Slug == slug);
        Assert.Null(row.NoteName);
        Assert.Equal(ArticleHash.Compute(Encoding.UTF8.GetBytes("текст"), [], ""), row.ContentHash);
    }

    [Fact]
    public async Task Too_long_note_name_is_not_stored_but_is_hashed()
    {
        var (factory, client) = StartWithToken();
        var slug = UniqueSlug();
        var name = new string('я', 201);
        var content = Article("текст");
        content.Add(new StringContent(name), "name");

        var response = await client.PutAsync($"/api/articles/{slug}", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        var row = db.Articles.Single(article => article.Slug == slug);
        Assert.Null(row.NoteName);
        Assert.Equal(ArticleHash.Compute(Encoding.UTF8.GetBytes("текст"), [], "", name), row.ContentHash);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
