using Microsoft.AspNetCore.StaticFiles;

namespace Samizdat.Server.Storage;

/// Отдача вложения статьи. Файл приходит из vault как есть, и браузер не должен исполнить его
/// в origin сайта.
public static class AttachmentFile
{
    public const string Policy = "sandbox; default-src 'none'; style-src 'unsafe-inline'";

    static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// Растровые картинки, видео и аудио кода не несут, их можно открыть прямо во вкладке.
    static readonly HashSet<string> InlineTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/avif", "image/bmp", "image/x-icon",
        "video/mp4", "video/webm", "video/ogg", "video/quicktime",
        "audio/mpeg", "audio/ogg", "audio/wav", "audio/webm", "audio/mp4", "audio/flac",
    };

    public static IResult Serve(HttpContext context, string path)
    {
        context.Response.Headers.ContentSecurityPolicy = Policy;
        context.Response.Headers.XContentTypeOptions = "nosniff";

        var name = Path.GetFileName(path);
        var type = ContentTypes.TryGetContentType(path, out var found) ? found : "application/octet-stream";
        if (InlineTypes.Contains(type)) return Results.File(path, type, enableRangeProcessing: true);

        // SVG может нести <script>. Тег <img> заголовок attachment не читает и картинку показывает,
        // а прямой адрес только скачивает файл.
        if (type == "image/svg+xml") return Results.File(path, type, name);
        return Results.File(path, "application/octet-stream", name);
    }
}
