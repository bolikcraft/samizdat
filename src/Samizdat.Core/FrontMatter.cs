namespace Samizdat.Core;

public sealed class FrontMatter
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Slug { get; set; }
    public string? Theme { get; set; }
    public DateOnly? Date { get; set; }
    public bool Publish { get; set; }
}

public sealed record ParsedDocument(FrontMatter FrontMatter, string Body);

public sealed class FrontMatterException(string message, Exception? inner = null)
    : Exception(message, inner);
