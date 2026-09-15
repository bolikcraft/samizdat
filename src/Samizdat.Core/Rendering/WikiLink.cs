using Markdig.Syntax.Inlines;

namespace Samizdat.Core.Rendering;

public sealed class WikiLink : LeafInline
{
    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".avif"];

    public required string Target { get; init; }
    public string? Label { get; init; }
    public bool IsEmbed { get; init; }

    public string Text => Label ?? Target;

    /// Эмбед картинки, а не обычная ссылка или вложение другого типа.
    public bool IsPictureEmbed => IsEmbed && IsImage(Target);

    public static bool IsImage(string target)
        => ImageExtensions.Contains(Path.GetExtension(Path.GetFileName(target)), StringComparer.OrdinalIgnoreCase);
}
