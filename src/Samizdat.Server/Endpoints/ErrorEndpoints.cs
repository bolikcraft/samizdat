using Microsoft.AspNetCore.Diagnostics;
using Samizdat.Core.Localization;
using Samizdat.Core.Themes;

namespace Samizdat.Server.Endpoints;

public static class ErrorEndpoints
{
    /// Ловит необработанные исключения до аутентификации: клиенту — короткий ответ без деталей,
    /// подробности (тип, сообщение, пути) — только в лог сервера.
    public static void MapErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler(branch => branch.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
            context.RequestServices.GetRequiredService<ILogger<Program>>()
                .LogError(error, "Необработанная ошибка на {Path}", context.Request.Path);

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;

            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Внутренняя ошибка");
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            try
            {
                var pages = context.RequestServices.GetRequiredService<PageRenderer>();
                var text = context.RequestServices.GetRequiredService<Translator>();
                await context.Response.WriteAsync(pages.Render("500.html", new()
                {
                    ["page_title"] = text["error.failed.title"],
                    ["site"] = new Dictionary<string, object?> { ["title"] = "Samizdat" },
                }));
            }
            catch (ThemeException)
            {
                // В теме нет шаблона 500.html (например, самодельная тема без него) — не роняем и это.
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Внутренняя ошибка");
            }
        }));
    }
}
