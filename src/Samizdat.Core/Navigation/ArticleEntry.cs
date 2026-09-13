namespace Samizdat.Core.Navigation;

public sealed record ArticleEntry(string Folder, string Slug, string Title);

public sealed record ArticleLink(string Slug, string Title, bool IsCurrent);
