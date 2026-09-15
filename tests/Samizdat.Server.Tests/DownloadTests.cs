using System.IO.Compression;
using System.Text;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Tests;

public class ArticlePackageTests
{
    [Fact]
    public void The_archive_holds_one_folder_with_the_source_and_the_attachments()
    {
        var root = Directory.CreateTempSubdirectory("samizdat-zip").FullName;
        try
        {
            var picture = Path.Combine(root, "ezh.png");
            File.WriteAllBytes(picture, [1, 2, 3]);

            var bytes = ArticlePackage.Pack("tayna", Encoding.UTF8.GetBytes("текст"),
                                            [("ezh.png", picture)]);

            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            Assert.Equal(["tayna/index.md", "tayna/ezh.png"], zip.Entries.Select(entry => entry.FullName));

            using var source = new StreamReader(zip.GetEntry("tayna/index.md")!.Open());
            Assert.Equal("текст", source.ReadToEnd());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
