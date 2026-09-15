using Samizdat.Core;

namespace Samizdat.Core.Tests;

public class PublicSourceTests
{
    [Fact]
    public void Foreign_fields_are_dropped()
    {
        var source = """
                     ---
                     title: Тайна
                     tags: [заметки, devops]
                     publish: true
                     zametka-dlya: Пети
                     ---

                     Текст.
                     """;

        var result = PublicSource.Of(source);

        Assert.DoesNotContain("tags", result);
        Assert.DoesNotContain("publish", result);
        Assert.DoesNotContain("Пети", result);
        Assert.Contains("Текст.", result);
    }

    [Fact]
    public void Title_description_and_date_stay_in_this_order()
    {
        var source = """
                     ---
                     date: 2026-09-10
                     description: Короткая шпаргалка
                     title: Тайна
                     ---

                     Текст.
                     """;

        var result = PublicSource.Of(source);
        var parsed = FrontMatterParser.Parse(result);

        Assert.Equal("Тайна", parsed.FrontMatter.Title);
        Assert.Equal("Короткая шпаргалка", parsed.FrontMatter.Description);
        Assert.Equal(new DateOnly(2026, 9, 10), parsed.FrontMatter.Date);
        Assert.True(result.IndexOf("title:", StringComparison.Ordinal)
                    < result.IndexOf("description:", StringComparison.Ordinal));
        Assert.True(result.IndexOf("description:", StringComparison.Ordinal)
                    < result.IndexOf("date:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_single_title_gives_a_short_header()
    {
        var source = "---\ntitle: Тайна\n---\n\nТекст.\n";

        Assert.Equal("---\ntitle: Тайна\n---\n\nТекст.\n", PublicSource.Of(source));
    }

    [Fact]
    public void A_file_without_a_header_stays_as_it_is()
    {
        var source = "# Заголовок\n\nТекст.\n";

        Assert.Equal(source, PublicSource.Of(source));
    }

    [Fact]
    public void A_header_without_known_fields_disappears_completely()
    {
        var source = "---\ntags: [заметки]\n---\n\nТекст.\n";

        Assert.Equal("\nТекст.\n", PublicSource.Of(source));
    }

    [Fact]
    public void Special_characters_in_the_title_survive_a_round_trip()
    {
        var source = "---\ntitle: \"Postgres: как поднять\"\n---\n\nТекст.\n";

        var parsed = FrontMatterParser.Parse(PublicSource.Of(source));

        Assert.Equal("Postgres: как поднять", parsed.FrontMatter.Title);
    }

    [Fact]
    public void The_body_is_not_touched()
    {
        var source = "---\ntitle: Тайна\n---\n\nСмотри [[другую заметку]] и ![[ezh.png]].\n";

        var result = PublicSource.Of(source);

        Assert.Contains("[[другую заметку]]", result);
        Assert.Contains("![[ezh.png]]", result);
    }

    [Fact]
    public void A_broken_header_is_still_an_error()
        => Assert.Throws<FrontMatterException>(() => PublicSource.Of("---\ntitle: [не закрыт\n---\n\nТекст.\n"));
}
