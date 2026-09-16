using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;
using Testcontainers.PostgreSql;

namespace Samizdat.Server.Tests;

/// Своя чистая база: общая фикстура чистит таблицы, поэтому владельца из миграции там уже нет.
/// Хэш пароля в миграции — непрозрачная строка: опечатка в ней закроет вход на новом сервере,
/// и заметят это только при выкладке.
public class InitMigrationTests : IAsyncLifetime
{
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task Migration_creates_owner_admin_with_password_admin()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        var owner = await db.Users.SingleAsync();
        Assert.Equal("admin", owner.Login);
        Assert.Equal(UserRole.Owner, owner.Role);
        Assert.True(PasswordHasher.Verify("admin", owner.PasswordHash));
    }

    [Fact]
    public async Task Next_user_gets_a_free_id_after_the_seeded_owner()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        db.Users.Add(new UserRow
        {
            Login = "second",
            PasswordHash = PasswordHasher.Hash("x"),
            Role = UserRole.Reader,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var logins = await db.Users.OrderBy(user => user.Id).Select(user => user.Login).ToListAsync();
        Assert.Equal(["admin", "second"], logins);
    }

    // Заполнение колонки в миграции: без него существующие читатели на работающем сервере
    // остались бы с пустой датой, то есть без входа.
    [Fact]
    public async Task Migration_marks_the_seeded_owner_as_approved()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        Assert.NotNull((await db.Users.SingleAsync()).ApprovedAt);
    }

    // Ссылка, выданная до записи автора, переживает обновление: живая и без автора, то есть владельческая.
    [Fact]
    public async Task Link_made_before_the_author_column_stays_alive_and_without_an_author()
    {
        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("UserLanguage");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO articles ("Slug", "Title", "ContentHash", "IndexedHash", "SearchText", "UpdatedAt", "Visibility")
            VALUES ('statya', 'Статья', 'h', '', '', now(), 1);
            INSERT INTO share_links ("Token", "Slug", "CreatedAt", "OpenedCount")
            VALUES ('AAAAAAAAAAAAAAAAAAAAAA', 'statya', now(), 0);
            """);

        await db.Database.MigrateAsync();

        var link = await db.ShareLinks.SingleAsync();
        Assert.Null(link.CreatedByUserId);
        Assert.True(link.IsAlive(DateTimeOffset.UtcNow));
    }

    SamizdatDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SamizdatDbContext>()
            .UseNpgsql(container.GetConnectionString()).Options;
        return new SamizdatDbContext(options);
    }
}
