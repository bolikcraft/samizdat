using System.Text;
using Samizdat.Core;

namespace Samizdat.Core.Tests;

public class ArticleHashTests
{
    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Same_content_gives_same_hash()
        => Assert.Equal(ArticleHash.Compute(Bytes("a"), [], ""), ArticleHash.Compute(Bytes("a"), [], ""));

    [Fact]
    public void Different_markdown_gives_different_hash()
        => Assert.NotEqual(ArticleHash.Compute(Bytes("a"), [], ""), ArticleHash.Compute(Bytes("b"), [], ""));

    [Fact]
    public void Changed_attachment_changes_hash()
    {
        var before = ArticleHash.Compute(Bytes("a"), [("pic.png", Bytes("1"))], "");
        var after = ArticleHash.Compute(Bytes("a"), [("pic.png", Bytes("2"))], "");

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Attachment_order_does_not_matter()
    {
        var first = ArticleHash.Compute(Bytes("a"), [("b.png", Bytes("1")), ("a.png", Bytes("2"))], "");
        var second = ArticleHash.Compute(Bytes("a"), [("a.png", Bytes("2")), ("b.png", Bytes("1"))], "");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Folder_changes_hash()
        => Assert.NotEqual(ArticleHash.Compute(Bytes("a"), [], "Заметки"),
                           ArticleHash.Compute(Bytes("a"), [], "Другое"));

    [Fact]
    public void Same_folder_keeps_hash()
        => Assert.Equal(ArticleHash.Compute(Bytes("a"), [], "Заметки"),
                        ArticleHash.Compute(Bytes("a"), [], "Заметки"));

    // Эталон — SHA-256 от «a»: на эту формулу опираются старый CLI и плагин Obsidian.
    [Fact]
    public void Hash_without_a_name_stays_as_it_was()
        => Assert.Equal("ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb",
                        ArticleHash.Compute(Bytes("a"), [], "", name: null));

    [Fact]
    public void Name_changes_hash()
        => Assert.NotEqual(ArticleHash.Compute(Bytes("a"), [], ""),
                           ArticleHash.Compute(Bytes("a"), [], "", "Моя заметка"));

    [Fact]
    public void Renamed_file_changes_hash()
        => Assert.NotEqual(ArticleHash.Compute(Bytes("a"), [], "", "Старое имя"),
                           ArticleHash.Compute(Bytes("a"), [], "", "Новое имя"));
}
