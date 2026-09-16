using Samizdat.Core.Localization;
using Samizdat.Core.Themes;

namespace Samizdat.Core.Tests;

public class LanguagePackTests
{
    static readonly EmbeddedLanguageSource Source = new();

    public static TheoryData<string> Codes()
    {
        var codes = new TheoryData<string>();
        foreach (var code in Source.Codes().Where(code => code != "en"))
            codes.Add(code);
        return codes;
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Packs_carry_the_same_keys(string code)
    {
        var english = Source.Read("en")!.Keys.Order(StringComparer.Ordinal).ToList();
        var pack = Source.Read(code)!.Keys.Order(StringComparer.Ordinal).ToList();

        Assert.Equal([], pack.Except(english, StringComparer.Ordinal));
        Assert.Equal([], english.Except(pack, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Packs_keep_the_placeholders(string code)
    {
        var english = Source.Read("en")!;
        var pack = Source.Read(code)!;

        foreach (var (key, text) in english)
            foreach (var slot in new[] { "{0}", "{1}" })
                Assert.Equal(Count(text, slot), Count(pack[key], slot));
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Every_pack_names_itself_in_its_own_language(string code)
    {
        var name = Source.Read(code)!["language.name"];

        Assert.NotEqual("", name);
        Assert.NotEqual(Source.Read("en")!["language.name"], name);
    }

    static int Count(string text, string slot)
    {
        var found = 0;
        for (var at = text.IndexOf(slot, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(slot, at + slot.Length, StringComparison.Ordinal))
            found++;
        return found;
    }

    [Fact]
    public void Every_pack_builds_a_model()
    {
        var catalog = new LanguageCatalog(new EmbeddedLanguageSource());

        foreach (var language in catalog.Available())
            Assert.NotEmpty(catalog.For(language.Code).Model());
    }

    [Fact]
    public void No_template_keeps_cyrillic_text_outside_a_comment()
    {
        var theme = new EmbeddedThemeSource();
        foreach (var name in new[] { "layout.html", "index.html", "article.html", "search.html",
                                     "login.html", "register.html", "register-sent.html", "settings.html",
                                     "settings-nav.html", "share-panel.html", "share-expired.html",
                                     "nav-node.html", "403.html", "404.html", "500.html" })
        {
            // Комментарии шаблона ({{~## ... ##~}}) остаются по-русски: их читает тот, кто правит тему.
            var text = StripComments(theme.ReadText(name)!);
            Assert.DoesNotContain(text, letter => letter is >= 'А' and <= 'я');
        }
    }

    static string StripComments(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"\{\{~?##.*?##~?\}\}", "",
                                                        System.Text.RegularExpressions.RegexOptions.Singleline);
}
