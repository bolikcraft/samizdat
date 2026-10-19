using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

/// Тема печатает title в h1. Такой же заголовок первым блоком заметки выводить не нужно.
public class RepeatedTitleTests
{
    readonly ArticleRenderer renderer = new();

    string Render(string markdown, string? title) => renderer.Render(markdown, "s", NoArticles.Instance, title: title);

    [Fact]
    public void A_first_heading_equal_to_the_title_is_dropped()
    {
        var html = Render("# Тема\n\nтекст", "Тема");

        Assert.DoesNotContain("<h1", html);
        Assert.Contains("<p>текст</p>", html);
    }

    [Fact]
    public void A_first_heading_with_other_text_stays()
        => Assert.Contains("Другое</h1>", Render("# Другое\n\nтекст", "Тема"));

    [Fact]
    public void A_heading_that_is_not_the_first_block_stays()
        => Assert.Contains("Тема</h1>", Render("текст\n\n# Тема", "Тема"));

    [Fact]
    public void A_second_level_heading_stays()
        => Assert.Contains("Тема</h2>", Render("## Тема\n\nтекст", "Тема"));

    [Fact]
    public void Markup_in_the_heading_does_not_count()
        => Assert.DoesNotContain("<h1", Render("# **Тема**\n\nтекст", "Тема"));

    [Fact]
    public void Code_and_a_wiki_link_count_by_their_text()
        => Assert.DoesNotContain("<h1", Render("# Про `код` и [[Статья|ссылку]]\n\nтекст", "Про код и ссылку"));

    [Fact]
    public void An_entity_counts_as_its_character()
        => Assert.DoesNotContain("<h1", Render("# Tom &amp; Jerry\n\nтекст", "Tom & Jerry"));

    [Fact]
    public void Spaces_around_the_text_do_not_count()
        => Assert.DoesNotContain("<h1", Render("#   Тема   \n\nтекст", "  Тема "));

    [Fact]
    public void An_underlined_heading_is_dropped_too()
        => Assert.DoesNotContain("<h1", Render("Тема\n===\n\nтекст", "Тема"));

    [Fact]
    public void Letter_case_counts()
        => Assert.Contains("тема</h1>", Render("# тема\n\nтекст", "Тема"));

    [Fact]
    public void Without_a_title_the_heading_stays()
        => Assert.Contains("Тема</h1>", Render("# Тема\n\nтекст", null));

    [Fact]
    public void The_search_index_keeps_the_heading()
        => Assert.Contains("Тема", PlainText.Extract("# Тема\n\nтекст"));
}
