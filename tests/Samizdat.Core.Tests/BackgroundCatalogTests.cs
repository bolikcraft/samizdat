using Samizdat.Core.Themes;

namespace Samizdat.Core.Tests;

public class BackgroundCatalogTests
{
    sealed class FakeTheme(string? manifest) : IThemeSource
    {
        public string Version => "1";
        public Stream? OpenRead(string path) => null;
        public string? ReadText(string path) => path == "assets/backgrounds.json" ? manifest : null;
    }

    static BackgroundCatalog Read(string manifest) => BackgroundCatalog.Read(new FakeTheme(manifest));

    [Fact]
    public void A_picture_without_a_thumbnail_shows_itself_in_the_gallery()
    {
        var catalog = Read("""{ "images": [ { "file": "silk.svg", "title": "Шёлк" } ] }""");

        var image = Assert.Single(catalog.Images);
        Assert.Equal("silk.svg", image.Thumb);
    }

    [Fact]
    public void A_thumbnail_is_taken_from_the_manifest()
    {
        var catalog = Read("""
            { "images": [ { "file": "moss.webp", "title": "Мох", "thumb": "moss-thumb.webp" } ] }
            """);

        Assert.Equal("moss-thumb.webp", Assert.Single(catalog.Images).Thumb);
    }

    [Fact]
    public void Names_that_leave_the_folder_are_refused()
    {
        // Имя из файла темы уходит в адрес /assets/backgrounds/<файл>, поэтому путь тут недопустим.
        var catalog = Read("""
            {
              "images": [
                { "file": "../../secret.svg", "title": "Чужое" },
                { "file": "ok.svg", "title": "Своё", "thumb": "../thumb.svg" }
              ]
            }
            """);

        var image = Assert.Single(catalog.Images);
        Assert.Equal("ok.svg", image.File);
        // Негодная миниатюра не отменяет картинку: плитка просто покажет её саму.
        Assert.Equal("ok.svg", image.Thumb);
    }

    [Fact]
    public void Only_colors_written_as_hex_get_into_the_palette()
    {
        var catalog = Read("""{ "colors": [ "#3f7a6a", "red", "#12345", "javascript:alert(1)" ] }""");

        Assert.Equal(["#3f7a6a"], catalog.Colors);
        Assert.True(catalog.HasColor("#3F7A6A"));
    }

    [Fact]
    public void A_theme_without_the_manifest_or_with_a_broken_one_has_an_empty_set()
    {
        Assert.Empty(BackgroundCatalog.Read(new FakeTheme(null)).Images);
        Assert.Empty(Read("{ это не json }").Images);
    }
}
