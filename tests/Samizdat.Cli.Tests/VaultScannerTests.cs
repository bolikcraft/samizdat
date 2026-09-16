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

    void Attachment(string path, byte[] bytes)
    {
        var full = Path.Combine(vault, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
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

    [Fact]
    public void Note_in_subfolder_reports_its_folder()
    {
        Note("PROXMOX/immich.md", "---\ntitle: Immich\npublish: true\n---\nx");

        Assert.Equal("PROXMOX", new VaultScanner(vault).Scan().Single().Folder);
    }

    [Fact]
    public void Note_in_root_reports_empty_folder()
    {
        Note("a.md", "---\ntitle: A\npublish: true\n---\nx");

        Assert.Equal("", new VaultScanner(vault).Scan().Single().Folder);
    }

    [Fact]
    public void Nested_folders_use_forward_slashes()
    {
        Note("База знаний/Linux/alt.md", "---\ntitle: Alt\npublish: true\n---\nx");

        Assert.Equal("База знаний/Linux", new VaultScanner(vault).Scan().Single().Folder);
    }

    [Fact]
    public void Moving_a_note_between_folders_changes_its_hash()
    {
        Note("A/x.md", "---\ntitle: X\nslug: x\npublish: true\n---\nтекст");
        var before = new VaultScanner(vault).Scan().Single().Hash;

        Directory.CreateDirectory(Path.Combine(vault, "B"));
        File.Move(Path.Combine(vault, "A/x.md"), Path.Combine(vault, "B/x.md"));

        Assert.NotEqual(before, new VaultScanner(vault).Scan().Single().Hash);
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

    [Fact]
    public void Explicit_slug_longer_than_200_bytes_stops_scanning()
    {
        Note("a.md", $"---\ntitle: A\nslug: {new string('я', 101)}\npublish: true\n---\nx");

        var error = Assert.Throws<CliException>(() => new VaultScanner(vault).Scan().ToList());
        Assert.Contains(Path.Combine(vault, "a.md"), error.Message);
        Assert.Contains("200", error.Message);
    }

    // Встроенная заметка index.md из другой папки затёрла бы на сервере текст статьи.
    [Fact]
    public void Note_named_index_md_is_not_an_attachment()
    {
        Note("a.md", "---\ntitle: A\npublish: true\n---\n![[index.md]] и ![[схема.png]]");
        Note("docs/index.md", "чужая заметка");
        File.WriteAllBytes(Path.Combine(vault, "схема.png"), [1]);

        var names = new VaultScanner(vault).Scan().Single().Attachments.Select(item => item.Name);

        Assert.Equal(["схема.png"], names);
    }

    [Fact]
    public void Attachment_with_a_control_character_in_its_name_is_skipped()
    {
        Note("a.md", "---\ntitle: A\npublish: true\n---\n![[bad\u0001.png]]");
        File.WriteAllBytes(Path.Combine(vault, "bad\u0001.png"), [1]);

        Assert.Empty(new VaultScanner(vault).Scan().Single().Attachments);
    }

    [Fact]
    public void Note_reports_its_file_name_without_extension()
    {
        Note("Папка/Моя заметка.md", "---\ntitle: Как настроить сервер\npublish: true\n---\nx");

        var note = new VaultScanner(vault).Scan().Single();

        Assert.Equal("kak-nastroit-server", note.Slug);
        Assert.Equal("Моя заметка", note.Name);
    }

    // Иначе новое имя файла не дойдёт до сервера, и [[Новое имя]] не найдёт статью.
    [Fact]
    public void Renaming_the_file_changes_its_hash()
    {
        Note("old.md", "---\ntitle: X\nslug: x\npublish: true\n---\nтекст");
        var before = new VaultScanner(vault).Scan().Single().Hash;

        File.Move(Path.Combine(vault, "old.md"), Path.Combine(vault, "new.md"));

        Assert.NotEqual(before, new VaultScanner(vault).Scan().Single().Hash);
    }

    [Fact]
    public void Broken_front_matter_without_publish_is_skipped_silently()
    {
        Note("Templates/Note.md", "---\ntitle: {{title}}\ndate: {{date}}\n---\n");
        Note("a.md", "---\ntitle: A\npublish: true\n---\nx");

        var scanner = new VaultScanner(vault);

        Assert.Equal("a", scanner.Scan().Single().Slug);
        Assert.Empty(scanner.Warnings);
    }

    [Fact]
    public void Broken_front_matter_with_publish_false_is_skipped_with_a_warning()
    {
        Note("draft.md", "---\ntitle: {{title}}\npublish: false\n---\nx");

        var scanner = new VaultScanner(vault);

        Assert.Empty(scanner.Scan().ToList());
        Assert.Contains(Path.Combine(vault, "draft.md"), Assert.Single(scanner.Warnings));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("\"true\"")]
    [InlineData("yes # в работе")]
    public void Broken_front_matter_with_publish_true_stops_scanning(string value)
    {
        Note("a.md", $"---\ntitle: {{{{title}}}}\npublish: {value}\n---\nx");

        var error = Assert.Throws<CliException>(() => new VaultScanner(vault).Scan().ToList());
        Assert.Contains(Path.Combine(vault, "a.md"), error.Message);
    }

    [Theory]
    [InlineData("\"publish\"")]
    [InlineData("'publish'")]
    public void Broken_front_matter_with_quoted_publish_key_stops_scanning(string key)
    {
        Note("a.md", $"---\ntitle: {{{{title}}}}\n{key}: true\n---\nx");

        Assert.Throws<CliException>(() => new VaultScanner(vault).Scan().ToList());
    }

    [Fact]
    public void Second_scan_finds_an_attachment_added_after_the_first()
    {
        Note("n.md", "---\ntitle: N\npublish: true\n---\n![[pic.png]]");

        var scanner = new VaultScanner(vault);
        Assert.Empty(scanner.Scan().Single().Attachments);

        Attachment("sub/pic.png", [1]);

        Assert.Equal(new byte[] { 1 }, scanner.Scan().Single().Attachments.Single().Bytes);
    }

    [Fact]
    public void Scanning_twice_does_not_repeat_warnings()
    {
        Note("draft.md", "---\ntitle: {{title}}\npublish: false\n---\nx");

        var scanner = new VaultScanner(vault);
        scanner.Scan().ToList();
        scanner.Scan().ToList();

        Assert.Single(scanner.Warnings);
    }

    [Fact]
    public void Broken_front_matter_with_publish_true_stops_scanning_with_windows_line_ends()
    {
        Note("a.md", "---\r\ntitle: {{title}}\r\npublish: true\r\n---\r\nx");

        Assert.Throws<CliException>(() => new VaultScanner(vault).Scan().ToList());
    }

    [Fact]
    public void Embed_with_size_or_anchor_is_attached()
    {
        Note("a.md", "---\ntitle: A\npublish: true\n---\n![[scheme.png|300]] ![[photo.jpg|300x200]] ![[doc.pdf#page=2]]");
        File.WriteAllBytes(Path.Combine(vault, "scheme.png"), [1]);
        File.WriteAllBytes(Path.Combine(vault, "photo.jpg"), [2]);
        File.WriteAllBytes(Path.Combine(vault, "doc.pdf"), [3]);

        var names = new VaultScanner(vault).Scan().Single().Attachments
                                           .Select(item => item.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["doc.pdf", "photo.jpg", "scheme.png"], names);
    }

    [Fact]
    public void Relative_path_takes_the_file_next_to_the_note()
    {
        Note("A/note.md", "---\ntitle: A\npublish: true\n---\n![](img/cover.png)");
        Note("B/note.md", "---\ntitle: B\npublish: true\n---\n![](img/cover.png)");
        Attachment("A/img/cover.png", [1]);
        Attachment("B/img/cover.png", [2]);

        var notes = new VaultScanner(vault).Scan().ToDictionary(note => note.Slug);

        Assert.Equal(new byte[] { 1 }, notes["a"].Attachments.Single().Bytes);
        Assert.Equal(new byte[] { 2 }, notes["b"].Attachments.Single().Bytes);
    }

    [Fact]
    public void Path_from_vault_root_wins_over_a_nearer_file_with_the_same_name()
    {
        Note("notes/n.md", "---\ntitle: N\npublish: true\n---\n![[assets/pic.png]]");
        Attachment("assets/pic.png", [1]);
        Attachment("notes/deep/pic.png", [2]);

        var attachment = new VaultScanner(vault).Scan().Single().Attachments.Single();

        Assert.Equal("pic.png", attachment.Name);
        Assert.Equal(new byte[] { 1 }, attachment.Bytes);
    }

    [Fact]
    public void Name_only_link_takes_the_nearest_file()
    {
        Note("A/B/n.md", "---\ntitle: N\npublish: true\n---\n![[pic.png]]");
        Attachment("C/pic.png", [2]);
        Attachment("A/pics/pic.png", [1]);

        Assert.Equal(new byte[] { 1 }, new VaultScanner(vault).Scan().Single().Attachments.Single().Bytes);
    }

    [Fact]
    public void Files_at_equal_distance_are_chosen_by_path_order()
    {
        Note("n.md", "---\ntitle: N\npublish: true\n---\n![[pic.png]]");
        Attachment("Q/pic.png", [2]);
        Attachment("P/pic.png", [1]);

        Assert.Equal(new byte[] { 1 }, new VaultScanner(vault).Scan().Single().Attachments.Single().Bytes);
    }

    [Fact]
    public void Wildcards_in_the_name_are_plain_characters()
    {
        Note("n.md", "---\ntitle: N\npublish: true\n---\n![[*.png]] ![](?.pdf)");
        Attachment("pic.png", [1]);
        Attachment("a.pdf", [2]);

        Assert.Empty(new VaultScanner(vault).Scan().Single().Attachments);
    }

    [Fact]
    public void Relative_path_does_not_leave_the_vault()
    {
        var outside = Directory.CreateTempSubdirectory("samizdat-outside").FullName;
        try
        {
            var secret = Path.Combine(outside, "secret.png");
            File.WriteAllBytes(secret, [1]);
            var reference = Path.GetRelativePath(vault, secret).Replace('\\', '/');
            Note("n.md", $"---\ntitle: N\npublish: true\n---\n![]({reference})");

            Assert.Empty(new VaultScanner(vault).Scan().Single().Attachments);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose() => Directory.Delete(vault, recursive: true);
}
