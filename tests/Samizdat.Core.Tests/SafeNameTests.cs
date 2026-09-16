namespace Samizdat.Core.Tests;

public class SafeNameTests
{
    [Theory]
    [InlineData("privet-mir")]
    [InlineData("Привет")]
    [InlineData("日本語")]
    public void Normal_slug_has_no_problem(string slug) => Assert.Null(SafeName.SlugProblem(slug));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a\u0001b")]
    [InlineData("a\tb")]
    [InlineData("a\u007Fb")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    // ?, # и % в адресе значат «запрос», «якорь» и «процентный код» — ссылка на статью и
    // редирект на неё разъедутся с самим адресом.
    [InlineData("a%2fb")]
    [InlineData("a?b")]
    [InlineData("a#b")]
    public void Bad_slug_gets_a_reason(string slug) => Assert.NotNull(SafeName.SlugProblem(slug));

    [Fact]
    public void Slug_of_200_bytes_fits() => Assert.Null(SafeName.SlugProblem(new string('a', 200)));

    // 101 буква «я» — 202 байта: предел считается в байтах, а не в знаках.
    [Fact]
    public void Slug_over_200_bytes_is_refused() => Assert.NotNull(SafeName.SlugProblem(new string('я', 101)));

    [Theory]
    [InlineData("index.md")]
    [InlineData("INDEX.MD")]
    [InlineData("Index.Md")]
    [InlineData("a\\b.png")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("a/b.png")]
    [InlineData("bad\u0001.png")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    // Windows и zip отбрасывают хвостовые пробелы и точки: это тот же index.md.
    [InlineData("index.md ")]
    [InlineData("index.md.")]
    [InlineData("index.md...")]
    [InlineData("index.md  ")]
    [InlineData("INDEX.MD .")]
    public void Bad_attachment_name_is_refused(string name) => Assert.False(SafeName.IsAttachment(name));

    [Theory]
    [InlineData("схема.png")]
    [InlineData("日本.png")]
    [InlineData("index.md.png")]
    [InlineData("old-index.md")]
    public void Normal_attachment_name_is_accepted(string name) => Assert.True(SafeName.IsAttachment(name));
}
