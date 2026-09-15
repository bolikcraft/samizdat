using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Search;

namespace Samizdat.Server.Tests;

public class SearchSnippetTests
{
    [Fact]
    public void Marks_become_tags()
        => Assert.Equal("тут <mark>сервер</mark> стоит",
                        SearchSnippet.ToHtml($"тут {SearchSnippet.Start}сервер{SearchSnippet.Stop} стоит"));

    [Fact]
    public void Html_from_the_text_is_escaped()
        => Assert.Equal("&lt;script&gt;alert(1)&lt;/script&gt;",
                        SearchSnippet.ToHtml("<script>alert(1)</script>"));

    [Fact]
    public void Escaping_goes_before_the_marks()
    {
        var html = SearchSnippet.ToHtml($"{SearchSnippet.Start}<b>{SearchSnippet.Stop}");

        Assert.Equal("<mark>&lt;b&gt;</mark>", html);
    }

    [Fact]
    public void Fragment_break_becomes_a_visible_ellipsis()
        => Assert.Equal("первый кусок … второй кусок",
                        SearchSnippet.ToHtml($"первый кусок{SearchSnippet.FragmentBreak}второй кусок"));
}

[Collection("db")]
public class ArticleSearchTests(DatabaseFixture database) : IDisposable
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

    void AddArticle(WebApplicationFactory<Program> factory, string slug, string title, string text,
                    ArticleVisibility visibility, string? description = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Articles.Add(new ArticleRow
        {
            Slug = slug, Title = title, ContentHash = "h", IndexedHash = "h", SearchText = text,
            Description = description, Visibility = visibility, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    async Task<IReadOnlyList<SearchHit>> Find(WebApplicationFactory<Program> factory, string query, bool isOwner)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ArticleSearch>().Find(query, isOwner);
    }

    [Fact]
    public async Task Morphology_finds_a_word_in_another_form()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "В доме стоит тихий сервер.", ArticleVisibility.Private);

        var hits = await Find(factory, "серверы", isOwner: true);

        Assert.Equal("dom", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Snippet_shows_the_found_word()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "В доме стоит тихий сервер.", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        // Ordinal обязателен: в культурном сравнении управляющие символы невесомы, и проверка
        // зеленеет на цитате без подсветки.
        Assert.Contains($"{SearchSnippet.Start}сервер{SearchSnippet.Stop}", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Word_only_in_the_description_gets_a_highlighted_snippet()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "тут стоит тихий дом", ArticleVisibility.Private,
                   description: "рассказ про старый сервер в подвале");

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.Contains($"{SearchSnippet.Start}сервер{SearchSnippet.Stop}", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Word_in_both_description_and_text_still_shows_a_snippet()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "тут стоит тихий сервер в доме", ArticleVisibility.Private,
                   description: "рассказ про домашний сервер");

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.Contains($"{SearchSnippet.Start}сервер{SearchSnippet.Stop}", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_description_leaves_no_leading_garbage_in_the_snippet()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом",
                   "Сервер стоит в углу комнаты и совсем негромко гудит уже которую ночь подряд.",
                   ArticleVisibility.Private, description: "");

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        // Пустое описание не должно оставить перед цитатой ни пробела, ни своего разделителя.
        Assert.StartsWith($"{SearchSnippet.Start}Сервер{SearchSnippet.Stop}", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Phrase_in_quotes_is_a_phrase()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "odna", "Одна", "тихий сервер в доме", ArticleVisibility.Private);
        AddArticle(factory, "drugaya", "Другая", "сервер шумит, дом тихий", ArticleVisibility.Private);

        var hits = await Find(factory, "\"тихий сервер\"", isOwner: true);

        Assert.Equal("odna", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Minus_word_takes_an_article_away()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "odna", "Одна", "сервер в доме", ArticleVisibility.Private);
        AddArticle(factory, "drugaya", "Другая", "сервер в офисе", ArticleVisibility.Private);

        var hits = await Find(factory, "сервер -офис", isOwner: true);

        Assert.Equal("odna", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Broken_query_gives_an_empty_list()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "текст", ArticleVisibility.Private);

        Assert.Empty(await Find(factory, "\"\"\" & | !", isOwner: true));
    }

    [Fact]
    public async Task Reader_does_not_find_a_private_article_by_its_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайна", "тут стоит сервер", ArticleVisibility.Private);

        Assert.Empty(await Find(factory, "сервер", isOwner: false));
    }

    [Fact]
    public async Task Reader_finds_a_private_article_by_its_title_without_a_snippet()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайный сервер", "секретный текст", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "серверы", isOwner: false));

        Assert.False(hit.CanOpen);
        Assert.Null(hit.Snippet);
    }

    [Fact]
    public async Task Reader_finds_a_shared_article_by_its_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "otkrytaya", "Открытая", "тут стоит сервер", ArticleVisibility.Shared);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: false));

        Assert.True(hit.CanOpen);
        Assert.NotNull(hit.Snippet);
    }

    [Fact]
    public async Task Owner_finds_a_private_article_with_a_snippet()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайна", "тут стоит сервер", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.True(hit.CanOpen);
        Assert.NotNull(hit.Snippet);
    }

    [Fact]
    public async Task Title_weighs_more_than_the_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "v-tekste", "Про дом", "тут стоит сервер", ArticleVisibility.Private);
        AddArticle(factory, "v-zagolovke", "Мой сервер", "текст про дом", ArticleVisibility.Private);

        var hits = await Find(factory, "сервер", isOwner: true);

        Assert.Equal("v-zagolovke", hits[0].Slug);
    }

    [Fact]
    public async Task Trigrams_catch_a_typo()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "proxmox", "Proxmox", "гипервизор дома", ArticleVisibility.Private);

        using var scope = factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ArticleSearch>();

        Assert.Empty(await search.Find("proxmoks", isOwner: true));
        Assert.Equal("proxmox", Assert.Single(await search.FindSimilar("proxmoks", isOwner: true)).Slug);
    }

    [Fact]
    public async Task Huge_query_does_not_break_the_search()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "тут стоит сервер", ArticleVisibility.Private);

        // Запрос в мегабайт база не берёт: tsquery такого размера — отказ 54000.
        Assert.Empty(await Find(factory, new string('я', 1_300_000), isOwner: true));
    }

    [Fact]
    public async Task Zero_byte_in_the_query_does_not_break_the_search()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "тут стоит сервер", ArticleVisibility.Private);

        var hits = await Find(factory, "сервер\0", isOwner: true);

        Assert.Equal("dom", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Word_in_the_first_letters_of_a_long_query_still_finds_the_article()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "тут стоит сервер", ArticleVisibility.Private);

        // Хвост за пределом обрезки уходит целиком: иначе он добавил бы к запросу слово,
        // которого в статье нет, и находки не стало бы.
        var query = $"сервер{new string(' ', ArticleSearch.MaxQuery)}абракадабра";

        var hits = await Find(factory, query, isOwner: true);

        Assert.Equal("dom", Assert.Single(hits).Slug);
    }

    [Fact]
    public async Task Trigrams_catch_a_swapped_pair_of_letters()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "kubernetes", "Kubernetes", "оркестратор", ArticleVisibility.Private);

        using var scope = factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ArticleSearch>();

        Assert.Equal("kubernetes", Assert.Single(await search.FindSimilar("kuberentes", isOwner: true)).Slug);
    }

    [Fact]
    public async Task Trigrams_do_not_catch_a_word_of_another_article()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "proxmox", "Proxmox", "гипервизор дома", ArticleVisibility.Private);

        using var scope = factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ArticleSearch>();

        Assert.Empty(await search.FindSimilar("черепаха", isOwner: true));
    }

    [Fact]
    public async Task Trigrams_keep_the_text_of_a_private_article_closed()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tayna", "Тайна", "гипервизор", ArticleVisibility.Private);

        using var scope = factory.Services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<ArticleSearch>();

        Assert.Empty(await search.FindSimilar("гипервизер", isOwner: false));
    }

    // Обход ts_headline (экранируем перед вызовом, снимаем экранирование после — см. Find)
    // держится на верном порядке пяти replace() в обе стороны. Сторожевые тесты на случай,
    // если будущая чистка переставит их местами и подмену никто не заметит: xUnit сравнивает
    // строки культурно, поэтому Ordinal обязателен — иначе разница в этих символах невесома.

    [Fact]
    public async Task Snippet_keeps_raw_special_characters_byte_for_byte()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "znaki", "Знаки", "сервер & < > \" ' рядом", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.Contains("& < > \" ' рядом", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handwritten_amp_entity_does_not_turn_into_an_ampersand()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "amp", "Амперсанд", "сервер написан как &amp; в тексте", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.Contains("&amp;", hit.Snippet, StringComparison.Ordinal);
        Assert.DoesNotContain("&amp;amp;", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handwritten_tag_like_entity_stays_as_is()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "tag", "Тег", "сервер &lt;тег&gt; рядом", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.Contains("&lt;тег&gt;", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Emoji_next_to_the_found_word_survives()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "emoji", "Эмодзи", "сервер 🚀 гудит рядом", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.Contains("🚀", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_article_with_a_match_in_the_middle_gets_ellipsis_on_both_ends()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var before = string.Join(" ", Enumerable.Repeat("слово", 40));
        var after = string.Join(" ", Enumerable.Repeat("слово", 40));
        AddArticle(factory, "dom", "Дом", $"{before} тут стоит старый сервер в подвале дома {after}",
                   ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.StartsWith(SearchSnippet.Ellipsis, hit.Snippet, StringComparison.Ordinal);
        Assert.EndsWith(SearchSnippet.Ellipsis, hit.Snippet, StringComparison.Ordinal);
    }

    // Разбиение на два фрагмента (два далёких совпадения) плюс авторское «…» рядом со вторым
    // совпадением — конструкция проверена напрямую на ts_headline. Со старым текстовым
    // разделителем " … " разбор границы между фрагментами и авторского многоточия опирался
    // на один и тот же текст; с управляющим символом эта путаница исключена в принципе, а
    // края (там, где реально обрезано) размечаются верно и не путаются с многоточием автора.
    [Fact]
    public async Task Two_distant_matches_join_correctly_next_to_the_authors_own_ellipsis()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var before = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"перед{i}"));
        var between = string.Join(" ", Enumerable.Range(1, 30).Select(i => $"между{i}"));
        var after = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"после{i}"));
        AddArticle(factory, "dom", "Дом", $"{before} сервер {between} межа … дом {after}",
                   ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер дом", isOwner: true));
        var html = SearchSnippet.ToHtml(hit.Snippet!);

        Assert.Contains("<mark>сервер</mark>", html, StringComparison.Ordinal);
        Assert.Contains("<mark>дом</mark>", html, StringComparison.Ordinal);
        // Авторское «…» рядом со вторым найденным словом дошло до читателя как было.
        Assert.Contains("межа … <mark>дом</mark>", html, StringComparison.Ordinal);
        // Оба края текста реально обрезаны (перед первым словом и после второго) — оба отмечены.
        Assert.StartsWith(SearchSnippet.Ellipsis, html, StringComparison.Ordinal);
        Assert.EndsWith(SearchSnippet.Ellipsis, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Short_article_that_fits_whole_has_no_ellipsis()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        AddArticle(factory, "dom", "Дом", "тут стоит тихий сервер", ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.DoesNotContain(SearchSnippet.Ellipsis, hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Match_at_the_start_of_a_long_article_has_no_leading_ellipsis()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var after = string.Join(" ", Enumerable.Repeat("слово", 40));
        AddArticle(factory, "dom", "Дом", $"Сервер стоит в подвале дома тихо и совсем незаметно {after}",
                   ArticleVisibility.Private);

        var hit = Assert.Single(await Find(factory, "сервер", isOwner: true));

        Assert.False(hit.Snippet!.StartsWith(SearchSnippet.Ellipsis, StringComparison.Ordinal));
        Assert.EndsWith(SearchSnippet.Ellipsis, hit.Snippet, StringComparison.Ordinal);
    }
}

[Collection("db")]
public class SearchPageTests(DatabaseFixture database) : IDisposable
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
    public async Task Owner_finds_an_article_by_a_word_of_its_text()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "dom", "---\ntitle: Дом\n---\n\nВ доме стоит тихий сервер."))
            .EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/search?q=серверы");

        Assert.Contains("href=\"/dom\"", html);
        Assert.Contains("<mark>", html);
    }

    [Fact]
    public async Task Page_without_a_query_asks_for_one()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = await TestLogin.AsOwner(factory);

        var answer = await client.GetAsync("/search");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("name=\"q\"", await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nothing_found_says_so()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = await TestLogin.AsOwner(factory);

        var html = await client.GetStringAsync("/search?q=мамонт");

        Assert.Contains("Ничего не нашлось", html);
    }

    [Fact]
    public async Task Typo_brings_the_guess()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "proxmox", "---\ntitle: Proxmox\n---\n\nгипервизор дома"))
            .EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/search?q=proxmoks");

        Assert.Contains("Возможно, вы искали", html);
        Assert.Contains("href=\"/proxmox\"", html);
    }

    [Fact]
    public async Task Reader_sees_a_private_article_without_a_link()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "tayna", "---\ntitle: Тайный сервер\n---\n\nсекретный текст"))
            .EnsureSuccessStatusCode();

        var client = await TestLogin.AsReader(factory);
        var html = await client.GetStringAsync("/search?q=серверы");

        Assert.Contains("Тайный сервер", html);
        Assert.DoesNotContain("href=\"/tayna\"", html);
        Assert.DoesNotContain("секретный", html);
    }

    [Fact]
    public async Task Tag_from_the_text_does_not_become_markup()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "kod", "---\ntitle: Код\n---\n\nтег `<script>alert(1)</script>` в сервере"))
            .EnsureSuccessStatusCode();

        var client = await TestLogin.AsOwner(factory);
        var html = await client.GetStringAsync("/search?q=сервере");

        Assert.Contains("&lt;script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact]
    public async Task Query_in_the_field_survives()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = await TestLogin.AsOwner(factory);

        var html = await client.GetStringAsync("/search?q=тихий+сервер");

        Assert.Contains("value=\"тихий сервер\"", html);
    }

    [Fact]
    public async Task Query_with_a_quote_does_not_break_the_field()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = await TestLogin.AsOwner(factory);

        var html = await client.GetStringAsync("/search?q=%22%3Cscript%3E");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Search_needs_a_password()
    {
        database.ResetDatabase();
        using var factory = CreateFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var answer = await client.GetAsync("/search?q=сервер");

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
    }

    [Fact]
    public async Task Header_of_a_guest_page_has_no_search()
    {
        // Гостевая страница по ссылке /s/{token}: там нет ни навигации, ни поиска.
        database.ResetDatabase();
        using var factory = CreateFactory();
        var api = TestPublisher.ClientWithToken(factory);
        (await TestPublisher.Push(api, "statya", "---\ntitle: Статья\n---\n\nТекст статьи."))
            .EnsureSuccessStatusCode();

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var article = db.Articles.Single(row => row.Slug == "statya");
            article.Visibility = ArticleVisibility.Shared;
            var link = new ShareLinkRow
            {
                Token = ShareToken.Create(), Slug = "statya", CreatedAt = DateTimeOffset.UtcNow,
            };
            db.ShareLinks.Add(link);
            db.SaveChanges();
            token = link.Token;
        }

        var html = await factory.CreateClient().GetStringAsync($"/s/{token}");

        Assert.DoesNotContain("action=\"/search\"", html);
    }
}
