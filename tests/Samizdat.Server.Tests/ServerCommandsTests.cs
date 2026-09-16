using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
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

    // ResetDatabase очищает users, поэтому сид admin/admin тесты заводят сами.
    async Task AddAdmin(WebApplicationFactory<Program> factory, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = "admin", PasswordHash = PasswordHasher.Hash(password), Role = UserRole.Owner,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Owner_set_with_a_new_login_removes_admin_with_the_default_password()
    {
        database.ResetDatabase();
        var factory = StartServer();
        await AddAdmin(factory, "admin");

        await ServerCommands.TryRun(["owner", "set", "aleks", "тайна"], factory.Services);

        Assert.Null(FindUser(factory, "admin"));
        Assert.NotNull(FindUser(factory, "aleks"));
    }

    [Fact]
    public async Task Owner_set_with_a_new_login_keeps_admin_with_a_changed_password()
    {
        database.ResetDatabase();
        var factory = StartServer();
        await AddAdmin(factory, "другой");

        await ServerCommands.TryRun(["owner", "set", "aleks", "тайна"], factory.Services);

        Assert.NotNull(FindUser(factory, "admin"));
        Assert.NotNull(FindUser(factory, "aleks"));
    }

    [Fact]
    public async Task Owner_set_for_admin_changes_only_the_password()
    {
        database.ResetDatabase();
        var factory = StartServer();
        await AddAdmin(factory, "admin");

        await ServerCommands.TryRun(["owner", "set", "admin", "новый"], factory.Services);

        var admin = FindUser(factory, "admin");
        Assert.NotNull(admin);
        Assert.True(PasswordHasher.Verify("новый", admin.PasswordHash));
    }

    [Fact]
    public async Task Token_new_goes_to_the_first_owner()
    {
        database.ResetDatabase();
        var factory = StartServer();
        await AddAdmin(factory, "другой");
        await ServerCommands.TryRun(["owner", "set", "aleks", "тайна"], factory.Services);

        await ServerCommands.TryRun(["token", "new", "laptop"], factory.Services);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Equal(FindUser(factory, "admin")!.Id, db.ApiTokens.Single().UserId);
    }

    [Fact]
    public async Task Reindex_rebuilds_the_index_of_every_article()
    {
        database.ResetDatabase();
        Directory.CreateDirectory(Path.Combine(dataRoot, "articles", "statya"));
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "articles", "statya", "index.md"),
                                     "---\ntitle: Статья\n---\n\nсвежий текст");

        using var factory = StartServer();
        using (var scope = factory.Services.CreateScope())
        {
            // Хэш совпадает — обычная достройка такую строку не тронет.
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow
            {
                Slug = "statya", Title = "Статья", ContentHash = "h1", IndexedHash = "h1",
                SearchText = "старый текст",
            });
            db.SaveChanges();
        }

        Assert.True(await ServerCommands.TryRun(["reindex"], factory.Services));

        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Contains("свежий текст", after.Articles.Single().SearchText);
    }

    [Fact]
    public async Task Reindex_does_not_stop_on_an_article_without_a_file()
    {
        database.ResetDatabase();
        Directory.CreateDirectory(Path.Combine(dataRoot, "articles", "statya"));
        await File.WriteAllTextAsync(Path.Combine(dataRoot, "articles", "statya", "index.md"),
                                     "---\ntitle: Статья\n---\n\nсвежий текст");

        using var factory = StartServer();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Articles.Add(new ArticleRow { Slug = "propavshaya", Title = "Пропавшая", ContentHash = "h1" });
            db.Articles.Add(new ArticleRow { Slug = "statya", Title = "Статья", ContentHash = "h2" });
            db.SaveChanges();
        }

        Assert.True(await ServerCommands.TryRun(["reindex"], factory.Services));

        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        Assert.Contains("свежий текст", after.Articles.Single(row => row.Slug == "statya").SearchText);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
