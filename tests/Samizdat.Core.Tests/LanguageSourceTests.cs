using Samizdat.Core.Localization;

namespace Samizdat.Core.Tests;

public class LanguageSourceTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("samizdat-lang").FullName;

    [Fact]
    public void Embedded_source_carries_the_packs_of_the_repository()
    {
        var source = new EmbeddedLanguageSource();

        Assert.Contains("en", source.Codes());
        Assert.Contains("ru", source.Codes());
        Assert.Contains("zh-Hans", source.Codes());
        Assert.NotNull(source.Read("en"));
        Assert.Null(source.Read("нет-такого"));
    }

    [Fact]
    public void Disk_key_wins_over_embedded_and_other_keys_stay()
    {
        File.WriteAllText(Path.Combine(folder, "en.json"), """{"nav.articles": "Notes"}""");
        var source = new LayeredLanguageSource(new DiskLanguageSource(folder), new EmbeddedLanguageSource());

        var pack = source.Read("en")!;

        Assert.Equal("Notes", pack["nav.articles"]);
        Assert.True(pack.ContainsKey("language.name"));
    }

    [Fact]
    public void Disk_file_adds_a_new_language()
    {
        File.WriteAllText(Path.Combine(folder, "de.json"), """{"language.name": "Deutsch"}""");
        var source = new LayeredLanguageSource(new DiskLanguageSource(folder), new EmbeddedLanguageSource());

        Assert.Contains("de", source.Codes());
        Assert.Equal("Deutsch", source.Read("de")!["language.name"]);
    }

    [Fact]
    public void Version_changes_after_disk_file_is_edited()
    {
        var source = new DiskLanguageSource(folder);
        var before = source.Version;

        File.WriteAllText(Path.Combine(folder, "de.json"), """{"language.name": "Deutsch"}""");

        Assert.NotEqual(before, source.Version);
    }

    [Fact]
    public void Disk_source_refuses_to_escape_its_folder()
    {
        var source = new DiskLanguageSource(folder);

        Assert.Null(source.Read("../../etc/passwd"));
        Assert.Null(source.Read("../secret"));
    }

    [Fact]
    public void Disk_source_refuses_symlink_pointing_outside_its_folder()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"samizdat-lang-{Guid.NewGuid():N}.json");
        File.WriteAllText(secret, """{"language.name": "чужое"}""");
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "leak.json"), secret);

            Assert.Null(new DiskLanguageSource(folder).Read("leak"));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Fact]
    public void Broken_json_on_disk_throws_with_the_file_name()
    {
        File.WriteAllText(Path.Combine(folder, "de.json"), "{ это не json");

        var error = Assert.Throws<LanguageException>(() => new DiskLanguageSource(folder).Read("de"));

        Assert.Contains("de.json", error.Message);
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);
}
