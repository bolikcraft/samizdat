using Samizdat.Core.Navigation;

namespace Samizdat.Core.Tests;

public class ArticleTreeTests
{
    static ArticleEntry Entry(string folder, string slug, string title) => new(folder, slug, title);

    [Fact]
    public void Root_articles_land_in_the_root_node()
    {
        var tree = ArticleTree.Build([Entry("", "a", "А"), Entry("", "b", "Б")]);

        Assert.Empty(tree.Folders);
        Assert.Equal(["a", "b"], tree.Articles.Select(article => article.Slug));
    }

    [Fact]
    public void Folders_become_nested_nodes()
    {
        var tree = ArticleTree.Build([Entry("PROXMOX", "immich", "Immich")]);

        var folder = Assert.Single(tree.Folders);
        Assert.Equal("PROXMOX", folder.Name);
        Assert.Equal("PROXMOX", folder.Path);
        Assert.Equal("immich", Assert.Single(folder.Articles).Slug);
    }

    [Fact]
    public void Deep_paths_create_the_whole_chain()
    {
        var tree = ArticleTree.Build([Entry("База/Linux", "alt", "Alt")]);

        var first = Assert.Single(tree.Folders);
        var second = Assert.Single(first.Folders);
        Assert.Equal("Linux", second.Name);
        Assert.Equal("База/Linux", second.Path);
        Assert.Empty(first.Articles);
    }

    [Fact]
    public void Folders_come_before_articles_and_both_are_sorted()
    {
        var tree = ArticleTree.Build([
            Entry("", "ya", "Я"), Entry("", "a", "А"),
            Entry("Яма", "x", "Икс"), Entry("Аз", "y", "Игрек"),
        ]);

        Assert.Equal(["Аз", "Яма"], tree.Folders.Select(folder => folder.Name));
        Assert.Equal(["А", "Я"], tree.Articles.Select(article => article.Title));
    }

    [Fact]
    public void Same_folder_is_not_duplicated()
    {
        var tree = ArticleTree.Build([Entry("A", "x", "Икс"), Entry("A", "y", "Игрек")]);

        Assert.Single(tree.Folders);
        Assert.Equal(2, tree.Folders[0].Articles.Count);
    }

    [Fact]
    public void Node_knows_whether_it_holds_the_current_article()
    {
        var tree = ArticleTree.Build([Entry("A/B", "x", "Икс")], currentSlug: "x");

        Assert.True(tree.Folders[0].HasCurrent);
        Assert.True(tree.Folders[0].Folders[0].HasCurrent);
        Assert.True(tree.Folders[0].Folders[0].Articles[0].IsCurrent);
    }

    [Fact]
    public void Folder_name_with_spaces_is_kept_as_is()
    {
        var tree = ArticleTree.Build([Entry("База знаний", "x", "X")]);

        Assert.Equal("База знаний", Assert.Single(tree.Folders).Name);
    }

    [Fact]
    public void Very_deep_nesting_does_not_overflow_the_stack()
    {
        var folder = string.Join('/', Enumerable.Range(0, 500).Select(i => $"f{i}"));
        var tree = ArticleTree.Build([Entry(folder, "x", "X")], currentSlug: "x");

        var node = tree;
        for (var i = 0; i < 500; i++)
        {
            node = Assert.Single(node.Folders);
            Assert.Equal($"f{i}", node.Name);
            Assert.True(node.HasCurrent);
        }
        Assert.True(Assert.Single(node.Articles).IsCurrent);
    }

    [Fact]
    public void Articles_with_the_same_title_keep_a_stable_relative_order()
    {
        var tree = ArticleTree.Build([Entry("", "second", "Заметка"), Entry("", "first", "Заметка")]);

        Assert.Equal(["second", "first"], tree.Articles.Select(article => article.Slug));
    }

    [Fact]
    public void Article_without_a_title_sorts_before_titled_ones()
    {
        var tree = ArticleTree.Build([Entry("", "b", "Б"), Entry("", "empty", "")]);

        Assert.Equal(["empty", "b"], tree.Articles.Select(article => article.Slug));
    }

    [Fact]
    public void No_entries_produce_an_empty_root()
    {
        var tree = ArticleTree.Build([]);

        Assert.Empty(tree.Folders);
        Assert.Empty(tree.Articles);
        Assert.False(tree.HasCurrent);
    }
}
