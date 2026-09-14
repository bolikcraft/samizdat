namespace Samizdat.Core.Navigation;

public static class ArticleTree
{
    public static TreeNode Build(IEnumerable<ArticleEntry> entries, string? currentSlug = null)
    {
        var root = new TreeNode { Name = "", Path = "" };

        foreach (var entry in entries)
        {
            var node = root;
            if (entry.Folder.Length > 0)
                foreach (var part in entry.Folder.Split('/'))
                    node = Child(node, part);

            node.Articles.Add(new ArticleLink(entry.Slug, entry.Title, entry.Slug == currentSlug, entry.IsShared));
        }

        Sort(root);
        MarkCurrent(root);
        return root;
    }

    static TreeNode Child(TreeNode parent, string name)
    {
        var found = parent.Folders.FirstOrDefault(folder => folder.Name == name);
        if (found is not null) return found;

        var created = new TreeNode
        {
            Name = name,
            Path = parent.Path.Length == 0 ? name : $"{parent.Path}/{name}",
        };
        parent.Folders.Add(created);
        return created;
    }

    static void Sort(TreeNode node)
    {
        // OrderBy is a stable sort: articles with equal titles keep their original relative order.
        // List<T>.Sort is not guaranteed stable and would shuffle those ties unpredictably.
        var folders = node.Folders.OrderBy(folder => folder.Name, StringComparer.CurrentCulture).ToList();
        node.Folders.Clear();
        node.Folders.AddRange(folders);

        var articles = node.Articles.OrderBy(article => article.Title, StringComparer.CurrentCulture).ToList();
        node.Articles.Clear();
        node.Articles.AddRange(articles);

        foreach (var folder in node.Folders) Sort(folder);
    }

    static bool MarkCurrent(TreeNode node)
    {
        var here = node.Articles.Any(article => article.IsCurrent);
        foreach (var folder in node.Folders)
            here |= MarkCurrent(folder);

        node.HasCurrent = here;
        return here;
    }
}
