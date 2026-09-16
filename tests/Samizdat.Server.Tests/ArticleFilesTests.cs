using System.Runtime.Versioning;
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
    [InlineData("a\\b")]
    [InlineData("..\\..\\evil")]
    [InlineData("a\u0001b")]
    public void IsValidSlug_rejects_bad_values(string slug) => Assert.False(ArticleFiles.IsValidSlug(slug));

    [Theory]
    [InlineData("s")]
    [InlineData("privet-mir")]
    [InlineData("Привет")]
    [InlineData("日本語")]
    public void IsValidSlug_accepts_normal_values(string slug) => Assert.True(ArticleFiles.IsValidSlug(slug));

    // Маршруты не различают регистр, поэтому "S" занимает адрес /s/ так же, как "s".
    [Theory]
    [InlineData("s")]
    [InlineData("S")]
    public void IsReservedSlug_covers_both_letter_cases(string slug) => Assert.True(ArticleFiles.IsReservedSlug(slug));

    // Все эти сегменты — литеральные маршруты, которые побеждают /{slug}: статья с таким именем
    // была бы недоступна за своим адресом.
    [Theory]
    [InlineData("i")]
    [InlineData("I")]
    [InlineData("login")]
    [InlineData("Login")]
    [InlineData("register")]
    [InlineData("settings")]
    [InlineData("assets")]
    [InlineData("background")]
    [InlineData("Background")]
    public void IsReservedSlug_covers_every_literal_route(string slug) => Assert.True(ArticleFiles.IsReservedSlug(slug));

    [Fact]
    public void IsReservedSlug_leaves_ordinary_slugs_alone() => Assert.False(ArticleFiles.IsReservedSlug("privet-mir"));

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

    // Каталог новой версии на момент Rollback всегда свежесозданный (переехал из staging), поэтому
    // права из прошлого запроса на него уже не действуют — отбирать их нужно после BeginReplace,
    // не до него. Это и воспроизводит отказ Rollback, который должен ловить ApiEndpoints.
    // Права доступа Unix: тест воспроизводим только на Linux/macOS, как и Testcontainers-стенд в CI.
    [Fact]
    [SupportedOSPlatform("linux")]
    public void Rollback_can_fail_and_reports_the_same_exception_types_ApiEndpoints_catches()
    {
        files.Replace("s", "v1"u8.ToArray(), []);
        var write = files.BeginReplace("s", "v2"u8.ToArray(), []);
        var target = files.Folder("s");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var error = Record.Exception(() => write.Rollback());

            Assert.True(error is IOException or UnauthorizedAccessException,
                $"неожиданный тип исключения: {error?.GetType()}");
        }
        finally
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Download_is_a_reserved_slug()
        => Assert.True(ArticleFiles.IsReservedSlug("download"));

    [Fact]
    public void Attachments_list_everything_but_the_source()
    {
        files.Replace("tayna", "текст"u8.ToArray(), [("ezh.png", [1, 2, 3]), ("abc.png", [4])]);

        var names = files.Attachments("tayna").Select(one => one.Name).ToList();

        // По алфавиту: имена в архиве должны лежать одинаково от прогона к прогону.
        Assert.Equal(["abc.png", "ezh.png"], names);
    }

    [Fact]
    public void A_symlink_out_of_the_folder_is_not_an_attachment()
    {
        files.Replace("tayna", "текст"u8.ToArray(), []);

        var outside = Path.Combine(dataRoot, "chuzhoy.txt");
        File.WriteAllText(outside, "не наше");
        File.CreateSymbolicLink(Path.Combine(files.Folder("tayna"), "ssylka.txt"), outside);

        Assert.Empty(files.Attachments("tayna"));
    }

    [Fact]
    public void Markdown_bytes_come_back_untouched()
    {
        byte[] written = [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'\r', (byte)'\n'];
        files.Replace("tayna", written, []);

        Assert.Equal(written, files.ReadMarkdownBytes("tayna"));
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
