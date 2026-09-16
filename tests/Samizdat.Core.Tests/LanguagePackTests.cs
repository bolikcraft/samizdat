using Samizdat.Core.Localization;
using Samizdat.Core.Themes;

namespace Samizdat.Core.Tests;

public class LanguagePackTests
{
    [Fact]
    public void Packs_carry_the_same_keys()
    {
        var source = new EmbeddedLanguageSource();
        var english = source.Read("en")!.Keys.Order(StringComparer.Ordinal).ToList();
        var russian = source.Read("ru")!.Keys.Order(StringComparer.Ordinal).ToList();

        Assert.Equal([], russian.Except(english, StringComparer.Ordinal));
        Assert.Equal([], english.Except(russian, StringComparer.Ordinal));
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
