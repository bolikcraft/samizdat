using Markdig.Syntax.Inlines;

namespace Samizdat.Core.Rendering;

public sealed class WikiLink : LeafInline
{
    public required string Target { get; init; }
    public string? Label { get; init; }
    public bool IsEmbed { get; init; }

    public string Text => Label ?? Target;
}
