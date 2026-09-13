using Samizdat.Server.Storage;

namespace Samizdat.Server.Tests;

public class BackgroundFileTests : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-bg").FullName;

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8];
    static byte[] Png() => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    static byte[] Webp() => "RIFF    WEBP"u8.ToArray();

    /// Рвётся на первом же чтении — имитирует обрыв соединения посреди Save.
    sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    public void Extension_comes_from_the_first_bytes(string expected)
    {
        var bytes = expected switch { ".jpg" => Jpeg(), ".png" => Png(), _ => Webp() };
        var actual = BackgroundFile.ExtensionOf(bytes);

        // Assert.NotNull сужает actual до string: иначе компилятор из-за string? в Assert.Equal
        // цепляет перегрузку для ReadOnlySpan и падает предупреждением о nullability (тут — ошибкой).
        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void A_file_of_another_kind_has_no_extension()
    {
        Assert.Null(BackgroundFile.ExtensionOf("MZ this is not a picture"u8));
    }

    [Fact]
    public void A_file_shorter_than_the_signature_has_no_extension()
    {
        Assert.Null(BackgroundFile.ExtensionOf(Webp()[..(BackgroundFile.HeadLength - 1)]));
    }

    [Fact]
    public void Save_writes_the_file_and_reports_its_name()
    {
        var background = new BackgroundFile(dataRoot);

        var name = background.Save(new MemoryStream(Jpeg()), ".jpg");

        Assert.Equal("background.jpg", name);
        Assert.Equal(Jpeg(), File.ReadAllBytes(Path.Combine(dataRoot, "background", name)));
    }

    [Fact]
    public void Save_refuses_an_extension_it_cannot_serve()
    {
        var background = new BackgroundFile(dataRoot);

        Assert.Throws<ArgumentException>(() => background.Save(new MemoryStream(Jpeg()), ".exe"));
    }

    [Fact]
    public void The_second_save_leaves_one_file_in_the_folder()
    {
        var background = new BackgroundFile(dataRoot);
        background.Save(new MemoryStream(Jpeg()), ".jpg");

        var name = background.Save(new MemoryStream(Png()), ".png");

        Assert.Equal("background.png", name);
        Assert.Equal(["background.png"], Directory.EnumerateFiles(Path.Combine(dataRoot, "background"))
            .Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public void A_failing_copy_leaves_the_old_background_readable()
    {
        var background = new BackgroundFile(dataRoot);
        background.Save(new MemoryStream(Jpeg()), ".jpg");

        Assert.Throws<IOException>(() => background.Save(new ThrowingStream(), ".png"));

        Assert.Equal(["background.jpg"], Directory.EnumerateFiles(Path.Combine(dataRoot, "background"))
            .Select(f => Path.GetFileName(f)!).ToArray());
        var found = background.Open("background.jpg");
        Assert.NotNull(found);
        using var stream = found.Content;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(Jpeg(), memory.ToArray());
    }

    [Fact]
    public void Open_reads_back_what_save_wrote()
    {
        var background = new BackgroundFile(dataRoot);
        var name = background.Save(new MemoryStream(Webp()), ".webp");

        var found = background.Open(name);

        Assert.NotNull(found);
        using var stream = found.Content;
        Assert.Equal("image/webp", found.ContentType);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(Webp(), memory.ToArray());
    }

    [Fact]
    public void Open_accepts_an_uppercase_extension()
    {
        var background = new BackgroundFile(dataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "background"));
        File.WriteAllBytes(Path.Combine(dataRoot, "background", "background.JPG"), Jpeg());

        var found = background.Open("background.JPG");

        Assert.NotNull(found);
        using var stream = found.Content;
        Assert.Equal("image/jpeg", found.ContentType);
    }

    [Fact]
    public void Open_returns_nothing_for_an_empty_name()
    {
        Assert.Null(new BackgroundFile(dataRoot).Open(""));
    }

    [Fact]
    public void Open_returns_nothing_when_the_file_is_missing()
    {
        Assert.Null(new BackgroundFile(dataRoot).Open("background.jpg"));
    }

    // Настройку в базе может поправить кто угодно руками: имя из неё не должно уводить за каталог.
    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/background.jpg")]
    public void Open_refuses_a_name_that_is_not_ours(string name)
    {
        var background = new BackgroundFile(dataRoot);
        background.Save(new MemoryStream(Jpeg()), ".jpg");

        Assert.Null(background.Open(name));
    }

    [Fact]
    public void Open_refuses_a_file_of_an_unknown_extension()
    {
        var background = new BackgroundFile(dataRoot);
        // Save сама не пишет чужие расширения, поэтому файл кладём напрямую — так тест проверяет
        // именно проверку расширения в Open, а не то, что файла нет на диске.
        Directory.CreateDirectory(Path.Combine(dataRoot, "background"));
        File.WriteAllBytes(Path.Combine(dataRoot, "background", "background.exe"), Jpeg());

        Assert.Null(background.Open("background.exe"));
    }

    [Fact]
    public void Version_is_the_write_time_of_the_file()
    {
        var background = new BackgroundFile(dataRoot);
        var name = background.Save(new MemoryStream(Jpeg()), ".jpg");
        var moment = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(dataRoot, "background", name), moment);

        Assert.Equal(moment.Ticks.ToString(), background.Version(name));
    }

    [Fact]
    public void Version_is_empty_for_an_empty_name()
    {
        Assert.Equal("", new BackgroundFile(dataRoot).Version(""));
    }

    [Fact]
    public void Version_is_empty_when_the_file_is_missing()
    {
        Assert.Equal("", new BackgroundFile(dataRoot).Version("background.jpg"));
    }

    [Fact]
    public void Remove_deletes_the_file()
    {
        var background = new BackgroundFile(dataRoot);
        var name = background.Save(new MemoryStream(Jpeg()), ".jpg");

        background.Remove();

        Assert.Null(background.Open(name));
    }

    [Fact]
    public void Remove_without_a_background_does_nothing()
    {
        new BackgroundFile(dataRoot).Remove();
    }
}
