using System.Reflection;

namespace Samizdat.Core.Themes;

public sealed class EmbeddedThemeSource : IThemeSource
{
    static readonly Assembly Owner = typeof(EmbeddedThemeSource).Assembly;

    public string Version { get; } = Owner.GetName().Version?.ToString() ?? "1";

    public Stream? OpenRead(string path) => Owner.GetManifestResourceStream($"theme/{path}");

    public string? ReadText(string path)
    {
        using var stream = OpenRead(path);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
