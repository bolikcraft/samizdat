using System.Text;

namespace Samizdat.Core.Tests;

public class SluggerTests
{
    [Theory]
    [InlineData("Привет, мир!", "privet-mir")]
    [InlineData("Обновление Proxmox 8.2", "obnovlenie-proxmox-8-2")]
    [InlineData("  Ёжик   в  тумане ", "ezhik-v-tumane")]
    [InlineData("Щи & борщ", "schi-borsch")]
    [InlineData("---", "bez-nazvaniya")]
    public void Makes_url_safe_slug(string title, string expected)
        => Assert.Equal(expected, Slugger.FromTitle(title));

    [Fact]
    public void Result_is_stable_for_same_input()
        => Assert.Equal(Slugger.FromTitle("Тест"), Slugger.FromTitle("Тест"));

    [Fact]
    public void Try_gives_nothing_when_there_is_nothing_to_translate()
        => Assert.Null(Slugger.TryFromTitle("---"));

    [Theory]
    [InlineData("Привет, мир!")]
    [InlineData("Без названия")]
    public void Try_gives_the_same_slug_as_FromTitle(string title)
        => Assert.Equal(Slugger.FromTitle(title), Slugger.TryFromTitle(title));

    // Адреса уже выложенных русских заметок меняться не должны: на них ведут закладки и ссылки.
    [Theory]
    [InlineData("Съешь же ещё этих мягких французских булок, да выпей чаю",
                "sesh-zhe-esche-etih-myagkih-francuzskih-bulok-da-vypey-chayu")]
    [InlineData("Ёлка и йогурт", "elka-i-yogurt")]
    [InlineData("Щука, объём, подъезд", "schuka-obem-podezd")]
    [InlineData("ЭХО Юга Я", "eho-yuga-ya")]
    [InlineData("Proxmox и Docker: 2 ноды", "proxmox-i-docker-2-nody")]
    [InlineData("mix Привет 日本", "mix-privet")]
    public void Russian_titles_keep_their_slugs(string title, string expected)
        => Assert.Equal(expected, Slugger.FromTitle(title));

    [Theory]
    [InlineData("Über", "uber")]
    [InlineData("Café", "cafe")]
    [InlineData("Straße", "strasse")]
    [InlineData("Łódź", "lodz")]
    [InlineData("Æsir ø", "aesir-o")]
    public void Diacritics_are_dropped(string title, string expected)
        => Assert.Equal(expected, Slugger.FromTitle(title));

    [Theory]
    [InlineData("Київ", "kiyiv")]
    [InlineData("Ґанок і їжак, є", "ganok-i-yizhak-ye")]
    [InlineData("Вўліца", "vulica")]
    public void Ukrainian_and_belarusian_letters_are_transliterated(string title, string expected)
        => Assert.Equal(expected, Slugger.FromTitle(title));

    [Theory]
    [InlineData("日本語", "日本語")]
    [InlineData("مرحبا بالعالم", "مرحبا-بالعالم")]
    [InlineData("हिन्दी", "हिन्दी")]
    [InlineData("Αθήνα", "αθήνα")]
    public void Script_without_latin_keeps_its_letters(string title, string expected)
        => Assert.Equal(expected, Slugger.FromTitle(title));

    [Fact]
    public void Two_different_non_latin_titles_do_not_share_a_slug()
        => Assert.NotEqual(Slugger.FromTitle("日本語"), Slugger.FromTitle("中文"));

    [Fact]
    public void Title_in_nfd_gives_the_same_slug_as_in_nfc()
        => Assert.Equal("moy-ezh", Slugger.FromTitle("Мой ёж".Normalize(NormalizationForm.FormD)));

    [Fact]
    public void Long_title_is_cut_to_200_bytes_without_a_trailing_dash()
    {
        var slug = Slugger.FromTitle(string.Concat(Enumerable.Repeat("Щука ", 60)));

        Assert.True(Encoding.UTF8.GetByteCount(slug) <= SafeName.MaxSlugBytes);
        Assert.False(slug.EndsWith('-'));
        Assert.StartsWith("schuka-schuka", slug);
    }

    // Три байта на знак: 66 знаков — 198 байт, 67-й уже не влезает.
    [Fact]
    public void Long_non_latin_title_is_cut_on_a_letter_boundary()
        => Assert.Equal(string.Concat(Enumerable.Repeat("日本語", 22)),
                        Slugger.FromTitle(string.Concat(Enumerable.Repeat("日本語", 40))));
}
