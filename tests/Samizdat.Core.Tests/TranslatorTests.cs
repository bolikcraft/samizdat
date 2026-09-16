using Samizdat.Core.Localization;

namespace Samizdat.Core.Tests;

public class TranslatorTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("samizdat-lang").FullName;

    LanguageCatalog Catalog()
        => new(new LayeredLanguageSource(new DiskLanguageSource(folder), new EmbeddedLanguageSource()));

    [Fact]
    public void Missing_key_falls_back_to_english_then_to_the_key_itself()
    {
        // Ключ выдуман: во встроенных пакетах его нет, иначе русский взял бы строку оттуда.
        File.WriteAllText(Path.Combine(folder, "en.json"), """{"tale.only_english": "Articles"}""");
        File.WriteAllText(Path.Combine(folder, "ru.json"), """{"language.name": "Русский"}""");

        var text = Catalog().For("ru");

        Assert.Equal("Articles", text["tale.only_english"]);
        Assert.Equal("нет.такого", text["нет.такого"]);
    }

    [Fact]
    public void Unknown_language_falls_back_to_english()
    {
        File.WriteAllText(Path.Combine(folder, "en.json"), """{"nav.articles": "Articles"}""");

        var text = Catalog().For("кто-то-стёр-пакет");

        Assert.Equal("en", text.Code);
        Assert.Equal("Articles", text["nav.articles"]);
    }

    [Fact]
    public void Format_puts_values_into_the_string()
    {
        File.WriteAllText(Path.Combine(folder, "en.json"), """{"pass.short": "At least {0} characters"}""");

        Assert.Equal("At least 8 characters", Catalog().For("en").Format("pass.short", 8));
    }

    [Fact]
    public void Available_lists_codes_with_their_own_names()
    {
        File.WriteAllText(Path.Combine(folder, "de.json"), """{"language.name": "Deutsch"}""");

        var names = Catalog().Available();

        Assert.Contains(new Language("de", "Deutsch"), names);
        Assert.Contains(new Language("en", "English"), names);
    }

    [Fact]
    public void Pack_is_reread_after_a_disk_file_changes()
    {
        var file = Path.Combine(folder, "en.json");
        File.WriteAllText(file, """{"nav.articles": "Articles"}""");
        var catalog = Catalog();
        Assert.Equal("Articles", catalog.For("en")["nav.articles"]);

        File.WriteAllText(file, """{"nav.articles": "Notes"}""");
        // Время правки ставится после записи и с запасом: иначе на грубых часах файловой системы
        // метка не изменится и тест покажет попадание в кэш там, где его нет.
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal("Notes", catalog.For("en")["nav.articles"]);
    }

    [Fact]
    public void Model_turns_dotted_keys_into_a_tree()
    {
        File.WriteAllText(Path.Combine(folder, "en.json"),
                          """{"nav.articles": "Articles", "login.title": "Sign in"}""");

        var model = Catalog().For("en").Model();

        var nav = Assert.IsType<Dictionary<string, object?>>(model["nav"]);
        Assert.Equal("Articles", nav["articles"]);
    }

    [Fact]
    public void Key_that_is_both_a_string_and_a_branch_is_refused()
    {
        File.WriteAllText(Path.Combine(folder, "en.json"), """{"nav": "Menu", "nav.articles": "Articles"}""");

        var error = Assert.Throws<LanguageException>(() => Catalog().For("en").Model());

        Assert.Contains("nav", error.Message);
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
