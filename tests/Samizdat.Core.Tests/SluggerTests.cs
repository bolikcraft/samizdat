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
}
