using Samizdat.Server.Storage;

namespace Samizdat.Server.Tests;

public class ArticleFilesTests : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-files").FullName;
    readonly ArticleFiles files;

    public ArticleFilesTests() => files = new ArticleFiles(dataRoot);

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("a/b")]
    public void IsValidSlug_rejects_bad_values(string slug) => Assert.False(ArticleFiles.IsValidSlug(slug));

    [Theory]
    [InlineData("s")]
    [InlineData("privet-mir")]
    [InlineData("Привет")]
    public void IsValidSlug_accepts_normal_values(string slug) => Assert.True(ArticleFiles.IsValidSlug(slug));

    // Маршруты не различают регистр, поэтому "S" занимает адрес /s/ так же, как "s".
    [Theory]
    [InlineData("s")]
    [InlineData("S")]
    public void IsReservedSlug_covers_both_letter_cases(string slug) => Assert.True(ArticleFiles.IsReservedSlug(slug));

    [Fact]
    public void Staging_and_backup_folders_are_hidden_from_the_article_list()
    {
        files.Replace("real", "текст"u8.ToArray(), []);
        Directory.CreateDirectory(Path.Combine(files.ArticlesRoot, ".tmp-real-abc"));
        Directory.CreateDirectory(Path.Combine(files.ArticlesRoot, ".old-real-abc"));

        Assert.Equal(["real"], files.AllSlugs());
    }

    [Fact]
    public void Attachment_inside_a_staging_folder_is_not_served()
    {
        var staging = Path.Combine(files.ArticlesRoot, ".tmp-real-abc");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "leak.png"), [1]);

        Assert.Null(files.AttachmentPath(".tmp-real-abc", "leak.png"));
    }

    [Fact]
    public void Attachment_outside_the_article_folder_is_not_served()
    {
        files.Replace("st", "текст"u8.ToArray(), []);
        File.WriteAllText(Path.Combine(files.ArticlesRoot, "tayna.txt"), "секрет");

        Assert.Null(files.AttachmentPath("st", "../tayna.txt"));
    }

    [Fact]
    public void Attachment_behind_a_folder_link_is_not_served()
    {
        files.Replace("st", "текст"u8.ToArray(), []);
        var secret = Path.Combine(dataRoot, "secretdir");
        Directory.CreateDirectory(secret);
        File.WriteAllText(Path.Combine(secret, "tayna.txt"), "секрет");
        Directory.CreateSymbolicLink(Path.Combine(files.Folder("st"), "d"), secret);

        Assert.Null(files.AttachmentPath("st", "d/tayna.txt"));
    }

    [Fact]
    public void Markdown_source_is_not_served_as_an_attachment()
    {
        files.Replace("st", "текст"u8.ToArray(), []);

        Assert.Null(files.AttachmentPath("st", "index.md"));
    }

    [Fact]
    public void Failed_replace_leaves_no_staging_folder_behind()
    {
        // Имя с NUL — гарантированный сбой записи файла, не зависящий от файловой системы.
        var badAttachments = new (string Name, byte[] Bytes)[] { ("bad\0name.png", [1]) };

        Assert.Throws<ArgumentException>(() => files.Replace("s", "текст"u8.ToArray(), badAttachments));

        Assert.False(Directory.Exists(files.Folder("s")));
        Assert.Empty(Directory.Exists(files.ArticlesRoot) ? Directory.EnumerateFileSystemEntries(files.ArticlesRoot) : []);
    }

    [Fact]
    public void Failed_replace_of_an_existing_article_keeps_the_old_version()
    {
        files.Replace("s", "v1"u8.ToArray(), []);
        var badAttachments = new (string Name, byte[] Bytes)[] { ("bad\0name.png", [1]) };

        Assert.Throws<ArgumentException>(() => files.Replace("s", "v2"u8.ToArray(), badAttachments));

        Assert.Equal("v1", files.ReadMarkdown("s"));
        Assert.Equal(["s"], files.AllSlugs());
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
