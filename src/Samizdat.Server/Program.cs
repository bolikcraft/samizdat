using Samizdat.Core.Rendering;
using Samizdat.Core.Themes;
using Samizdat.Server.Endpoints;
using Samizdat.Server.Rendering;
using Samizdat.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

var dataRoot = builder.Configuration["Samizdat:DataRoot"] ?? "data";
var themeName = builder.Configuration["Samizdat:Theme"] ?? "default";

builder.Services.AddSingleton(new ArticleFiles(dataRoot));
builder.Services.AddSingleton<IThemeSource>(new LayeredThemeSource(
    new DiskThemeSource(Path.Combine(dataRoot, "themes", themeName)),
    new EmbeddedThemeSource()));
builder.Services.AddSingleton(services => new PageRenderer(services.GetRequiredService<IThemeSource>()));
builder.Services.AddSingleton<ArticleRenderer>();
builder.Services.AddSingleton<IArticleLookup>(services =>
    new FolderArticleLookup(services.GetRequiredService<ArticleFiles>()));
builder.Services.AddSingleton<PageCache>();

var app = builder.Build();
app.MapPages();
app.Run();

/// Временно: список статей берётся с диска. Task 12 заменит его на базу.
public sealed class FolderArticleLookup(ArticleFiles files) : IArticleLookup
{
    public bool Exists(string slug) => Directory.Exists(files.Folder(slug));
}

public partial class Program; // нужен WebApplicationFactory в тестах
