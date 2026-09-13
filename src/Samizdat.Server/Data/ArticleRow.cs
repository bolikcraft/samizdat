namespace Samizdat.Server.Data;

public sealed class ArticleRow
{
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string Folder { get; set; } = "";
    public string? Description { get; set; }
    public DateOnly? Date { get; set; }
    public string? Theme { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
