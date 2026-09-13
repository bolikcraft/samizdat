using Samizdat.Cli;

namespace Samizdat.Cli.Tests;

public class VaultScannerTests : IDisposable
{
    readonly string vault = Directory.CreateTempSubdirectory("samizdat-vault").FullName;

    void Note(string path, string text)
    {
        var full = Path.Combine(vault, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void Takes_only_notes_with_publish_true()
    {
        Note("a.md", "---\ntitle: A\npublish: true\n---\nтекст");
        Note("b.md", "---\ntitle: B\n---\nтекст");
        Note("c.md", "текст без фронтматтера");

        var found = new VaultScanner(vault).Scan().ToList();

        Assert.Single(found);
        Assert.Equal("a", found[0].Slug);
    }

    [Fact]
    public void Slug_from_front_matter_wins_over_title()
    {
        Note("a.md", "---\ntitle: Привет\nslug: hello\npublish: true\n---\nтекст");

        Assert.Equal("hello", new VaultScanner(vault).Scan().Single().Slug);
    }

    [Fact]
    public void Slug_falls_back_to_transliterated_title()
    {
        Note("a.md", "---\ntitle: Привет мир\npublish: true\n---\nтекст");

        Assert.Equal("privet-mir", new VaultScanner(vault).Scan().Single().Slug);
    }

    [Fact]
    public void Collects_only_attachments_used_in_the_note()
    {
        Note("a.md", "---\ntitle: A\npublish: true\n---\n![[схема.png]] и ![подпись](лишнее.png)");
        File.WriteAllBytes(Path.Combine(vault, "схема.png"), [1]);
        File.WriteAllBytes(Path.Combine(vault, "лишнее.png"), [2]);
        File.WriteAllBytes(Path.Combine(vault, "чужое.png"), [3]);

        var names = new VaultScanner(vault).Scan().Single().Attachments.Select(item => item.Name).Order().ToList();

        Assert.Equal(["лишнее.png", "схема.png"], names);
    }

    [Fact]
    public void Two_notes_with_same_slug_raise_an_error()
    {
        Note("a.md", "---\ntitle: T\nslug: same\npublish: true\n---\nx");
        Note("sub/b.md", "---\ntitle: T2\nslug: same\npublish: true\n---\nx");

        Assert.Throws<CliException>(() => new VaultScanner(vault).Scan().ToList());
    }

    [Fact]
    public void Hidden_folders_are_skipped()
    {
        Note(".obsidian/plugins/x.md", "---\npublish: true\ntitle: X\n---\nx");

        Assert.Empty(new VaultScanner(vault).Scan());
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("admin/sub")]
    [InlineData("admin\\sub")]
    [InlineData("../x")]
    [InlineData(".hidden")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Bad_explicit_slug_stops_scanning_with_a_clear_error(string badSlugYaml)
    {
        Note("a.md", $"---\ntitle: A\nslug: {badSlugYaml}\npublish: true\n---\nx");

        var error = Assert.Throws<CliException>(() => new VaultScanner(vault).Scan().ToList());
        Assert.Contains(Path.Combine(vault, "a.md"), error.Message);
    }

    public void Dispose() => Directory.Delete(vault, recursive: true);
}
