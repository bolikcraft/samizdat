using System.Text.RegularExpressions;

namespace Samizdat.Core.Rendering;

/// Метка эмбеда картинки в Obsidian: "300" или "300x200" — размер, "подпись|300" — alt и размер.
public static partial class EmbedSize
{
    // [0-9], а не \d: \d в .NET пропускает цифры других письменностей.
    [GeneratedRegex(@"\A(?<width>[0-9]{1,5})(?:x(?<height>[0-9]{1,5}))?\z")]
    private static partial Regex Size();

    public static (string? Alt, string? Width, string? Height) Parse(string? label)
    {
        if (label is null) return (null, null, null);

        var bar = label.LastIndexOf('|');
        var size = Size().Match((bar < 0 ? label : label[(bar + 1)..]).Trim());
        if (!size.Success) return (label, null, null);

        var alt = bar < 0 ? null : label[..bar].Trim();
        return (alt is { Length: > 0 } ? alt : null,
                size.Groups["width"].Value,
                size.Groups["height"].Success ? size.Groups["height"].Value : null);
    }
}
