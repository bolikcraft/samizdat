using Microsoft.EntityFrameworkCore;
using Samizdat.Core.Rendering;
using Samizdat.Core.Themes;
using Samizdat.Server.Data;
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
builder.Services.AddSingleton<PageCache>();

builder.Services.AddDbContext<SamizdatDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
                      ?? "Host=localhost;Database=samizdat;Username=samizdat"));

builder.Services.AddScoped<IArticleLookup, DbArticleLookup>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<SamizdatDbContext>().Database.Migrate();

app.MapPages();
app.Run();

public partial class Program; // нужен WebApplicationFactory в тестах
