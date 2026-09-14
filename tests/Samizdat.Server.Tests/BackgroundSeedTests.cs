using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class BackgroundSeedTests(DatabaseFixture database)
{
    /// Первый запуск проверяем на отдельной базе: общая проходит через ResetDatabase, а он вычищает
    /// таблицы вместе со строками, которые кладут миграции.
    [Fact]
    public async Task A_fresh_site_starts_with_a_background_from_the_set()
    {
        var name = $"seed_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"""CREATE DATABASE "{name}" """, admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = name }.ToString();
        var options = new DbContextOptionsBuilder<SamizdatDbContext>().UseNpgsql(connection).Options;
        await using var db = new SamizdatDbContext(options);
        await db.Database.MigrateAsync();

        Assert.Equal("preset:moss.webp", db.Settings.Find("theme.background")?.Value);
        Assert.True(db.Users.Any(user => user.Login == "admin"));
    }

    [Fact]
    public async Task The_seed_leaves_the_background_of_a_working_site_alone()
    {
        var name = $"seed_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(database.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"""CREATE DATABASE "{name}" """, admin);
            await create.ExecuteNonQueryAsync();
        }

        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = name }.ToString();
        var options = new DbContextOptionsBuilder<SamizdatDbContext>().UseNpgsql(connection).Options;

        // Сайт, который уже жил на прошлой версии: миграции накатаны до предыдущей, а владелец
        // успел выбрать «без фона».
        await using (var older = new SamizdatDbContext(options))
        {
            await older.GetInfrastructure().GetRequiredService<IMigrator>().MigrateAsync("ArticleVisibility");
            await older.Database.ExecuteSqlRawAsync(
                """INSERT INTO site_settings ("Key", "Value") VALUES ('theme.background', '')""");
        }

        await using var db = new SamizdatDbContext(options);
        await db.Database.MigrateAsync();

        Assert.Equal("", db.Settings.Find("theme.background")?.Value);
    }
}
