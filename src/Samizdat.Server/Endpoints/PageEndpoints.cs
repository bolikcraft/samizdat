using Microsoft.AspNetCore.StaticFiles;
using Samizdat.Core;
using Samizdat.Core.Rendering;
using Samizdat.Core.Themes;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class PageEndpoints
{
    static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapPages(this WebApplication app)
    {
        app.MapGet("/", (PageRenderer pages, ArticleFiles files) =>
        {
            var list = files.AllSlugs().Order().Select(slug => new Dictionary<string, object?>
            {
                ["slug"] = slug,
                ["title"] = FrontMatterParser.Parse(files.ReadMarkdown(slug) ?? "").FrontMatter.Title ?? slug,
            }).ToList();

            return Results.Content(pages.Render("index.html", new()
            {
                ["page_title"] = "Samizdat",
                ["site"] = new Dictionary<string, object?> { ["title"] = "Samizdat" },
                ["articles"] = list,
            }), "text/html; charset=utf-8");
        });

        app.MapGet("/{slug}", (string slug, PageRenderer pages, ArticleFiles files,
                               ArticleRenderer markdown, IArticleLookup articles) =>
        {
            var text = files.ReadMarkdown(slug);
            if (text is null) return NotFound(pages);

            var parsed = FrontMatterParser.Parse(text);
            var html = markdown.Render(parsed.Body, slug, articles);

            return Results.Content(pages.Render("article.html", new()
            {
                ["page_title"] = parsed.FrontMatter.Title ?? slug,
                ["site"] = new Dictionary<string, object?> { ["title"] = "Samizdat" },
                ["article"] = new Dictionary<string, object?>
                {
                    ["slug"] = slug,
                    ["title"] = parsed.FrontMatter.Title ?? slug,
                    ["description"] = parsed.FrontMatter.Description,
                    ["date"] = parsed.FrontMatter.Date?.ToString("yyyy-MM-dd"),
                    ["html"] = html,
                },
            }), "text/html; charset=utf-8");
        });

        app.MapGet("/{slug}/{*file}", (string slug, string file, ArticleFiles files, PageRenderer pages) =>
        {
            var path = files.AttachmentPath(slug, file);
            if (path is null) return NotFound(pages);

            var type = ContentTypes.TryGetContentType(path, out var found) ? found : "application/octet-stream";
            return Results.File(path, type);
        });

        app.MapGet("/assets/{*file}", (string file, IThemeSource theme) =>
        {
            var stream = theme.OpenRead($"assets/{file}");
            if (stream is null) return Results.NotFound();

            var type = ContentTypes.TryGetContentType(file, out var found) ? found : "application/octet-stream";
            return Results.Stream(stream, type);
        });
    }

    static IResult NotFound(PageRenderer pages)
        => Results.Content(pages.Render("404.html", new() { ["page_title"] = "Не найдено" }),
                           "text/html; charset=utf-8", statusCode: 404);
}
