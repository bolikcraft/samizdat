namespace Samizdat.Core.Navigation;

public sealed class TreeNode
{
    public required string Name { get; init; }

    /// Полный путь от корня вольта: по нему тема запоминает, раскрыта ли папка.
    public required string Path { get; init; }

    public List<TreeNode> Folders { get; } = [];
    public List<ArticleLink> Articles { get; } = [];

    public bool HasCurrent { get; set; }
}
