using Microsoft.AspNetCore.StaticFiles;

namespace Samizdat.Server.Storage;

/// Отдача вложения статьи. Файл приходит из vault как есть, и браузер не должен исполнить его
/// в origin сайта.
public static class AttachmentFile
{
    public const string Policy = "sandbox; default-src 'none'; style-src 'unsafe-inline'";

    static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// Растровые картинки кода не несут, их можно открыть прямо во вкладке.
    static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "image/x-icon",
    };

    public static IResult Serve(HttpContext context, string path)
    {
        context.Response.Headers.ContentSecurityPolicy = Policy;
        context.Response.Headers.XContentTypeOptions = "nosniff";

        var name = Path.GetFileName(path);
        var type = ContentTypes.TryGetContentType(path, out var found) ? found : "application/octet-stream";
        if (InlineTypes.Contains(type)) return Results.File(path, type);

        // SVG может нести <script>. Тег <img> заголовок attachment не читает и картинку показывает,
        // а прямой адрес только скачивает файл.
        if (type == "image/svg+xml") return Results.File(path, type, name);
        return Results.File(path, "application/octet-stream", name);
    }
}
