using System.Text;

namespace Samizdat.Core;

/// Правила для slug и имён вложений. Сервер и CLI проверяют одним кодом: иначе CLI пропустит
/// заметку, которую сервер отвергнет посреди выкладки.
public static class SafeName
{
    /// Сервер держит рядом с каталогом статьи служебный .tmp-{slug}-{guid}, это ещё 38 байт.
    /// Имя каталога в Linux не длиннее 255 байт.
    public const int MaxSlugBytes = 200;

    /// Имя годится как один сегмент пути. На Linux «\» — обычный знак, но в zip и на Windows
    /// это разделитель: «..\..\x» при распаковке выходит из папки.
    public static bool IsSegment(string name)
        => name.Length > 0
           && name is not ("." or "..")
           && !name.Any(symbol => symbol is '/' or '\\' || char.IsControl(symbol));

    /// Исходник статьи лежит в одной папке с вложениями. Регистр не важен: на macOS и Windows
    /// INDEX.MD и index.md — один файл.
    public static bool IsSource(string name) => name.Equals("index.md", StringComparison.OrdinalIgnoreCase);

    public static bool IsAttachment(string name) => IsSegment(name) && !IsSource(name);

    /// null, если под этим slug можно выложить статью; иначе причина отказа.
    public static string? SlugProblem(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return "пустой slug";
        if (slug.Contains("..")) return $"slug «{slug}» не должен содержать «..»";
        if (slug.StartsWith('.')) return $"slug «{slug}» не должен начинаться с точки";
        if (!IsSegment(slug)) return $"slug «{slug}» не должен содержать /, \\ и управляющие символы";
        if (Encoding.UTF8.GetByteCount(slug) > MaxSlugBytes)
            return $"slug «{slug}» длиннее {MaxSlugBytes} байт в UTF-8";
        return null;
    }
}
