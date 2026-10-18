using System.Reflection;

namespace Samizdat.Core.Themes;

/// root — каталог ресурсов сборки: "theme" для встроенной темы, "app" для файлов ядра.
public sealed class EmbeddedThemeSource(string root = "theme") : IThemeSource
{
    static readonly Assembly Owner = typeof(EmbeddedThemeSource).Assembly;

    /// Файлы ядра: шаблоны служебных страниц и скрипты.
    public static EmbeddedThemeSource App { get; } = new("app");

    public string Version { get; } = Owner.GetName().Version?.ToString() ?? "1";

    public Stream? OpenRead(string path) => Owner.GetManifestResourceStream($"{root}/{path}");

    public string? ReadText(string path)
    {
        using var stream = OpenRead(path);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
