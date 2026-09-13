namespace Samizdat.Core.Themes;

/// Первый источник, где файл нашёлся, выигрывает.
public sealed class LayeredThemeSource(params IThemeSource[] layers) : IThemeSource
{
    public string Version => string.Join('|', layers.Select(layer => layer.Version));

    public Stream? OpenRead(string path)
        => layers.Select(layer => layer.OpenRead(path)).FirstOrDefault(found => found is not null);

    public string? ReadText(string path)
        => layers.Select(layer => layer.ReadText(path)).FirstOrDefault(found => found is not null);
}
