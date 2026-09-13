using Samizdat.Core;

namespace Samizdat.Core.Tests;

public class FrontMatterParserTests
{
    [Fact]
    public void Reads_fields_and_returns_body_without_front_matter()
    {
        var text = "---\ntitle: Привет\npublish: true\nslug: privet\ndate: 2026-09-13\n---\nТекст\n";

        var result = FrontMatterParser.Parse(text);

        Assert.Equal("Привет", result.FrontMatter.Title);
        Assert.True(result.FrontMatter.Publish);
        Assert.Equal("privet", result.FrontMatter.Slug);
        Assert.Equal(new DateOnly(2026, 9, 13), result.FrontMatter.Date);
        Assert.Equal("Текст\n", result.Body);
    }

    [Fact]
    public void File_without_front_matter_keeps_whole_text_as_body()
    {
        var result = FrontMatterParser.Parse("# Заголовок\nтекст\n");

        Assert.False(result.FrontMatter.Publish);
        Assert.Null(result.FrontMatter.Title);
        Assert.Equal("# Заголовок\nтекст\n", result.Body);
    }

    [Fact]
    public void Unknown_fields_do_not_break_parsing()
    {
        var result = FrontMatterParser.Parse("---\ntitle: T\ntags: [a, b]\ncssclass: wide\n---\nx");

        Assert.Equal("T", result.FrontMatter.Title);
    }

    [Fact]
    public void Broken_yaml_throws_with_line_number()
    {
        var text = "---\ntitle: [не закрыт\n---\nx";

        var error = Assert.Throws<FrontMatterException>(() => FrontMatterParser.Parse(text));

        Assert.Contains("2", error.Message);
    }

    [Fact]
    public void Windows_line_endings_are_supported()
    {
        var result = FrontMatterParser.Parse("---\r\ntitle: T\r\n---\r\nтело\r\n");

        Assert.Equal("T", result.FrontMatter.Title);
        Assert.Equal("тело\r\n", result.Body);
    }
}
