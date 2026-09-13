using System.Text;
using Samizdat.Core;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Samizdat.Server.Storage;

namespace Samizdat.Server.Endpoints;

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        // Bearer-токен, не cookie: antiforgery здесь неприменим в принципе, отключаем на всю группу
        // разом, чтобы не забыть про новые маршруты.
        var api = app.MapGroup("/api")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(ApiToken.Scheme)
                .RequireAuthenticatedUser())
            .DisableAntiforgery();

        api.MapGet("/state", (SamizdatDbContext db) =>
            Results.Ok(db.Articles.ToDictionary(article => article.Slug, article => article.ContentHash)));

        api.MapPut("/articles/{slug}", async (string slug, HttpRequest request,
                                              ArticleFiles files, SamizdatDbContext db) =>
        {
            if (!ArticleFiles.IsValidSlug(slug)) return Results.BadRequest("Плохой slug");

            var form = await request.ReadFormAsync();
            var folder = form["folder"].ToString();
            if (!ArticleFiles.IsValidFolder(folder)) return Results.BadRequest($"{slug}: плохая папка");

            var source = form.Files.GetFile("index.md");
            if (source is null) return Results.BadRequest("Нет файла index.md");

            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            var markdown = buffer.ToArray();

            var attachments = new List<(string Name, byte[] Bytes)>();
            foreach (var file in form.Files.Where(file => file.Name == "attachments"))
            {
                using var one = new MemoryStream();
                await file.CopyToAsync(one);
                attachments.Add((Path.GetFileName(file.FileName), one.ToArray()));
            }

            ParsedDocument parsed;
            try
            {
                parsed = FrontMatterParser.Parse(Encoding.UTF8.GetString(markdown));
            }
            catch (FrontMatterException error)
            {
                return Results.BadRequest($"{slug}/index.md: {error.Message}");
            }

            files.Replace(slug, markdown, attachments);

            var row = await db.Articles.FindAsync(slug) ?? db.Articles.Add(new ArticleRow
            {
                Slug = slug, Title = slug, ContentHash = "",
            }).Entity;

            row.Title = parsed.FrontMatter.Title ?? slug;
            row.Folder = folder;
            row.Description = parsed.FrontMatter.Description;
            row.Date = parsed.FrontMatter.Date;
            row.Theme = parsed.FrontMatter.Theme;
            row.ContentHash = ArticleHash.Compute(markdown, attachments, folder);
            row.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { slug, hash = row.ContentHash });
        });

        api.MapDelete("/articles/{slug}", async (string slug, ArticleFiles files, SamizdatDbContext db) =>
        {
            if (!ArticleFiles.IsValidSlug(slug)) return Results.BadRequest("Плохой slug");

            files.Remove(slug);
            var row = await db.Articles.FindAsync(slug);
            if (row is not null)
            {
                db.Articles.Remove(row);
                await db.SaveChangesAsync();
            }
            return Results.Ok();
        });

        api.MapGet("/articles/{slug}.md", (string slug, ArticleFiles files) =>
        {
            if (!ArticleFiles.IsValidSlug(slug)) return Results.BadRequest("Плохой slug");

            var text = files.ReadMarkdown(slug);
            return text is null
                ? Results.NotFound()
                : Results.Text(text, "text/markdown; charset=utf-8");
        });
    }
}
