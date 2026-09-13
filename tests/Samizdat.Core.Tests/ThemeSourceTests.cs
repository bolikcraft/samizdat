using Samizdat.Core.Themes;

namespace Samizdat.Core.Tests;

public class ThemeSourceTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("samizdat-theme").FullName;

    [Fact]
    public void Embedded_theme_has_all_required_files()
    {
        var theme = new EmbeddedThemeSource();

        foreach (var name in new[] { "layout.html", "article.html", "index.html", "login.html", "404.html" })
            Assert.NotNull(theme.ReadText(name));
    }

    [Fact]
    public void Disk_file_wins_over_embedded()
    {
        File.WriteAllText(Path.Combine(folder, "article.html"), "свой шаблон");
        var theme = new LayeredThemeSource(new DiskThemeSource(folder), new EmbeddedThemeSource());

        Assert.Equal("свой шаблон", theme.ReadText("article.html"));
        Assert.Contains("{{ site.title | html.escape }}", theme.ReadText("index.html"));
    }

    [Fact]
    public void Version_changes_after_disk_file_is_edited()
    {
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        var theme = new LayeredThemeSource(new DiskThemeSource(folder), new EmbeddedThemeSource());
        var before = theme.Version;

        File.WriteAllText(Path.Combine(folder, "assets/style.css".Replace('/', Path.DirectorySeparatorChar)), "x");

        Assert.NotEqual(before, theme.Version);
    }

    [Fact]
    public void Missing_file_returns_null()
        => Assert.Null(new EmbeddedThemeSource().ReadText("нет-такого.html"));

    [Fact]
    public void Disk_source_refuses_to_escape_theme_folder()
    {
        var theme = new DiskThemeSource(folder);

        Assert.Null(theme.ReadText("../../etc/passwd"));
        Assert.Null(theme.OpenRead("../../etc/passwd"));
    }

    [Fact]
    public void Disk_source_refuses_symlink_pointing_outside_theme_folder()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"samizdat-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(secret, "чужие данные");
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "leak.html"), secret);
            var theme = new DiskThemeSource(folder);

            Assert.Null(theme.ReadText("leak.html"));
            Assert.Null(theme.OpenRead("leak.html"));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
