namespace Samizdat.Core.Navigation;

// IsShared со значением по умолчанию: Core ничего не знает про права, он только несёт признак дальше.
public sealed record ArticleEntry(string Folder, string Slug, string Title, bool IsShared = true);

public sealed record ArticleLink(string Slug, string Title, bool IsCurrent, bool IsShared = true);
