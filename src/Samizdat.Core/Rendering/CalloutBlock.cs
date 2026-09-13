using Markdig.Syntax;

namespace Samizdat.Core.Rendering;

public sealed class CalloutBlock() : ContainerBlock(null)
{
    public required string Kind { get; init; }
    public required string Title { get; init; }
}
