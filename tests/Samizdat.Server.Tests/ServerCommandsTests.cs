using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Commands;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ServerCommandsTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    WebApplicationFactory<Program> StartServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    UserRow? FindUser(WebApplicationFactory<Program> factory, string login)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        return db.Users.FirstOrDefault(row => row.Login == login);
    }

    [Fact]
    public async Task Owner_set_with_config_arguments_writes_the_owner()
    {
        database.ResetDatabase();
        var factory = StartServer();

        var handled = await ServerCommands.TryRun(
            ["owner", "set", "aleks", "тайна", "--ConnectionStrings:Postgres=x", "--urls", "http://+:5000"],
            factory.Services);

        Assert.True(handled);
        var user = FindUser(factory, "aleks");
        Assert.NotNull(user);
        Assert.Equal(UserRole.Owner, user.Role);
    }

    [Fact]
    public async Task Owner_set_without_password_writes_nothing()
    {
        database.ResetDatabase();
        var factory = StartServer();

        var handled = await ServerCommands.TryRun(["owner", "set", "aleks"], factory.Services);

        Assert.True(handled);
        Assert.Null(FindUser(factory, "aleks"));
    }

    [Fact]
    public async Task Owner_set_replaces_the_password_of_a_known_owner()
    {
        database.ResetDatabase();
        var factory = StartServer();
        await ServerCommands.TryRun(["owner", "set", "aleks", "первый"], factory.Services);
        var first = FindUser(factory, "aleks")!;

        await ServerCommands.TryRun(["owner", "set", "aleks", "второй"], factory.Services);

        var user = FindUser(factory, "aleks");
        Assert.NotNull(user);
        Assert.NotEqual(first.PasswordHash, user.PasswordHash);
        Assert.Equal(UserRole.Owner, user.Role);
        // Пароль сменили с консоли — выданные cookie должны погаснуть, как и при смене из настроек.
        Assert.NotEqual(first.SessionStamp, user.SessionStamp);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
