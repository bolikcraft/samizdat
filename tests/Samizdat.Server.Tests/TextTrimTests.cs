using Samizdat.Server.Search;

namespace Samizdat.Server.Tests;

public class TextTrimTests
{
    [Fact]
    public void Shorter_than_the_limit_stays_untouched()
        => Assert.Equal("текст", TextTrim.Cut("текст", 10));

    [Fact]
    public void Exactly_at_the_limit_stays_untouched()
        => Assert.Equal("текст", TextTrim.Cut("текст", 5));

    [Fact]
    public void Emoji_on_the_border_is_cut_one_symbol_earlier()
    {
        // "😀" — суррогатная пара; её первая половина попадает ровно на позицию limit - 1.
        var text = new string('a', 4) + "😀" + "хвост";

        Assert.Equal(new string('a', 4), TextTrim.Cut(text, 5));
    }

    [Fact]
    public void Empty_string_stays_empty()
        => Assert.Equal("", TextTrim.Cut("", 0));
}
