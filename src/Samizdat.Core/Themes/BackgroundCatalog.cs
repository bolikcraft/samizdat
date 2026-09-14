using System.Text.Json;
using System.Text.RegularExpressions;

namespace Samizdat.Core.Themes;

public sealed record BackgroundImage(string File, string Title);

/// Набор фонов темы: картинки лежат в assets/backgrounds/, список и палитра — в assets/backgrounds.json.
/// Имена и цвета отсюда уезжают в настройки, в адреса и в разметку страницы, поэтому файл темы —
/// такой же чужой ввод, как форма: всё, что не проходит проверку, в набор не попадает.
public sealed partial class BackgroundCatalog
{
    public static readonly BackgroundCatalog Empty = new([], []);

    BackgroundCatalog(IReadOnlyList<BackgroundImage> images, IReadOnlyList<string> colors)
    {
        Images = images;
        Colors = colors;
    }

    public IReadOnlyList<BackgroundImage> Images { get; }

    public IReadOnlyList<string> Colors { get; }

    public bool HasImage(string file) => Images.Any(image => image.File == file);

    public bool HasColor(string color) => Colors.Contains(color, StringComparer.OrdinalIgnoreCase);

    public static BackgroundCatalog Read(IThemeSource theme)
    {
        var text = theme.ReadText("assets/backgrounds.json");
        if (text is null) return Empty;

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(text, Json);
        }
        catch (JsonException)
        {
            // Тема с битым файлом остаётся без набора: страница настроек всё равно должна открыться.
            return Empty;
        }
        if (manifest is null) return Empty;

        var images = (manifest.Images ?? [])
            .Where(image => image.File is not null && FileName().IsMatch(image.File))
            .Select(image => new BackgroundImage(image.File!, image.Title ?? image.File!))
            .ToList();
        var colors = (manifest.Colors ?? []).Where(color => Hex().IsMatch(color)).ToList();

        return new BackgroundCatalog(images, colors);
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    sealed record Manifest(List<ManifestImage>? Images, List<string>? Colors);

    sealed record ManifestImage(string? File, string? Title);

    /// Имя файла, а не путь: значение уходит в адрес /assets/backgrounds/<файл>.
    [GeneratedRegex(@"^[a-z0-9-]+\.(svg|jpg|jpeg|png|webp)$")]
    private static partial Regex FileName();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();
}
