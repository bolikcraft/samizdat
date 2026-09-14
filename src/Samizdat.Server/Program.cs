using Samizdat.Server;
using Samizdat.Server.Commands;

var builder = WebApplication.CreateBuilder(args);
Startup.ConfigureServices(builder);

var app = builder.Build();

if (await ServerCommands.TryRun(args, app.Services)) return;

Startup.Configure(app);
app.Run();

public partial class Program; // нужен WebApplicationFactory в тестах
