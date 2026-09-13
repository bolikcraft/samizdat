using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Samizdat.Core.Rendering;
using Samizdat.Core.Themes;
using Samizdat.Server.Auth;
using Samizdat.Server.Commands;
using Samizdat.Server.Data;
using Samizdat.Server.Endpoints;
using Samizdat.Server.Rendering;
using Samizdat.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

var dataRoot = builder.Configuration["Samizdat:DataRoot"] ?? "data";

builder.Services.AddSingleton(new ArticleFiles(dataRoot));
builder.Services.AddSingleton(new BackgroundFile(dataRoot));
builder.Services.AddSingleton(services =>
    new ThemeFactory(dataRoot, services.GetRequiredService<ILogger<ThemeFactory>>()));
builder.Services.AddScoped<SiteSettings>();
builder.Services.AddScoped<IThemeSource>(services =>
    services.GetRequiredService<ThemeFactory>().Get(services.GetRequiredService<SiteSettings>().ThemeName));
builder.Services.AddScoped(services => new PageRenderer(services.GetRequiredService<IThemeSource>()));
builder.Services.AddSingleton<ArticleRenderer>();
builder.Services.AddSingleton<PageCache>();

builder.Services.AddDbContext<SamizdatDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
                      ?? "Host=localhost;Database=samizdat;Username=samizdat"));

builder.Services.AddScoped<IArticleLookup, DbArticleLookup>();

// Апач-прокси стоит в соседнем контейнере, не на loopback — доверяем заголовку без ограничения по сети.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ApiToken.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery();

var app = builder.Build();

if (await ServerCommands.TryRun(args, app.Services)) return;

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<SamizdatDbContext>().Database.Migrate();

app.UseForwardedHeaders();
app.MapErrorHandling();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapAuth();
app.MapPages().RequireAuthorization();
app.MapShare();
app.MapApi();
app.MapSettings();
app.Run();

public partial class Program; // нужен WebApplicationFactory в тестах
