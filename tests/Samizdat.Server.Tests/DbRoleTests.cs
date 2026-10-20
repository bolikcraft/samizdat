using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Samizdat.Server.Data;
using Testcontainers.PostgreSql;

namespace Samizdat.Server.Tests;

/// Тесты deploy/db-role.sql — того же файла, что запускает update.sh.
/// Своя база на каждый тест: роль samizdat_app общая для всего сервера Postgres.
public class DbRoleTests : IAsyncLifetime
{
    const string AppPassword = "parol-prilozheniya";

    // Как в compose.yaml: первая роль сервера и база называются samizdat.
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("samizdat").WithUsername("samizdat").WithPassword("samizdat").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task New_database_is_migrated_by_the_app_role_without_superuser_rights()
    {
        await RunRoleScript(AppPassword);

        await using var db = CreateContext(AppConnectionString(AppPassword));
        await db.Database.MigrateAsync();

        Assert.Equal("admin", (await db.Users.SingleAsync()).Login);
        var rights = await db.Database
            .SqlQueryRaw<bool>("""
                SELECT rolsuper OR rolcreatedb OR rolcreaterole AS "Value"
                FROM pg_roles WHERE rolname = current_user
                """)
            .SingleAsync();
        Assert.False(rights);
    }

    [Fact]
    public async Task Old_schema_of_the_superuser_goes_to_the_app_role()
    {
        await using (var superuser = CreateContext(container.GetConnectionString()))
        {
            // Так выглядит схема после восстановления дампа по старой инструкции README.
            await superuser.Database.ExecuteSqlRawAsync("ALTER SCHEMA public OWNER TO samizdat");
            await superuser.GetService<IMigrator>().MigrateAsync("UserLanguage");
        }

        await RunRoleScript(AppPassword);

        await using var db = CreateContext(AppConnectionString(AppPassword));
        // Следующие миграции меняют таблицы, а ALTER TABLE разрешён только владельцу.
        await db.Database.MigrateAsync();
        db.Users.Add(new UserRow
        {
            Login = "second", PasswordHash = "x", Role = UserRole.Reader, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE probe (id int)");

        var foreign = await db.Database
            .SqlQueryRaw<string>("""
                SELECT relname AS "Value" FROM pg_class
                WHERE relnamespace = 'public'::regnamespace AND relkind IN ('r', 'p', 'v', 'm', 'S')
                  AND relowner <> 'samizdat_app'::regrole
                UNION ALL
                SELECT collname FROM pg_collation
                WHERE collnamespace = 'public'::regnamespace AND collowner <> 'samizdat_app'::regrole
                """)
            .ToListAsync();
        Assert.Empty(foreign);
    }

    [Fact]
    public async Task Second_run_sets_the_new_password()
    {
        await RunRoleScript(AppPassword);
        await RunRoleScript("novyi-parol");

        await using (var fresh = new NpgsqlConnection(AppConnectionString("novyi-parol")))
            await fresh.OpenAsync();
        await using var stale = new NpgsqlConnection(AppConnectionString(AppPassword));
        var error = await Assert.ThrowsAsync<PostgresException>(() => stale.OpenAsync());
        Assert.Equal(PostgresErrorCodes.InvalidPassword, error.SqlState);
    }

    // \setenv заменяет переменную, которую update.sh передаёт через docker compose exec -e.
    async Task RunRoleScript(string password)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "deploy", "db-role.sql"));
        var result = await container.ExecScriptAsync($"\\setenv SAMIZDAT_DB_PASSWORD '{password}'\n{script}");
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    string AppConnectionString(string password) =>
        new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "samizdat_app", Password = password,
        }.ConnectionString;

    static SamizdatDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<SamizdatDbContext>().UseNpgsql(connectionString).Options);
}
