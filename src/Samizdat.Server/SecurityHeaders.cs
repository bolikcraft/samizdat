using Microsoft.Net.Http.Headers;

namespace Samizdat.Server;

public static class SecurityHeaders
{
    /// style-src 'unsafe-inline' нужен теме: фон задаёт блок <style> в layout.html, плитки настроек и
    /// подсветка кода пишут атрибут style. Картинки, видео и фреймы статьи бывают на чужих сайтах.
    public const string PagePolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' http: https:; media-src 'self' http: https:; frame-src https:; "
        + "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    /// Ставить до обработчика ошибок: колбэк OnStarting переживает очистку ответа, и страница 500
    /// тоже получает политику.
    public static void UseSecurityHeaders(this WebApplication app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var response = context.Response;
                response.Headers.XContentTypeOptions = "nosniff";
                // Маршрут со своей политикой (вложения) не перетираем.
                if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true
                    && !response.Headers.ContainsKey(HeaderNames.ContentSecurityPolicy))
                    response.Headers.ContentSecurityPolicy = PagePolicy;
                return Task.CompletedTask;
            });
            return next(context);
        });
}
