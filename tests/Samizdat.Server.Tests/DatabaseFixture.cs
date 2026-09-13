using Microsoft.EntityFrameworkCore;
using Samizdat.Server.Data;
using Testcontainers.PostgreSql;

namespace Samizdat.Server.Tests;

/// Один контейнер Postgres на всю коллекцию тестов: поднимать его на каждый тест слишком долго.
/// Поэтому таблицы общие — каждый тест обязан очистить их через ResetDatabase перед своими проверками.
public sealed class DatabaseFixture : IAsyncLifetime
{
    readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public void ResetDatabase()
    {
        using var db = CreateContext();
        db.Database.ExecuteSqlRaw(
            "TRUNCATE TABLE articles, users, api_tokens, site_settings, share_links RESTART IDENTITY CASCADE");
    }

    SamizdatDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<SamizdatDbContext>().UseNpgsql(ConnectionString).Options;
        return new SamizdatDbContext(options);
    }
}

[CollectionDefinition("db")]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;
