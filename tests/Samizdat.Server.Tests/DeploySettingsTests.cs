using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class DeploySettingsTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public DeploySettingsTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    WebApplicationFactory<Program> StartServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

    // В контейнере домашний каталог не переживает обновление образа: ключи в нём
    // разлогинили бы всех после каждой выкладки.
    [Fact]
    public async Task Session_keys_are_kept_in_the_data_folder()
    {
        using var factory = StartServer();
        await TestLogin.AsOwner(factory);

        var keys = Path.Combine(dataRoot, "keys");
        Assert.True(Directory.Exists(keys), "Каталог ключей не создан");
        Assert.NotEmpty(Directory.GetFiles(keys, "key-*.xml"));
    }

    // Оба хоста в одном процессе могут прочитать и общий ~/.aspnet, поэтому падение без правки
    // доказывает предыдущий тест; этот проверяет, что ключи с диска реально принимаются.
    [Fact]
    public async Task Session_from_one_server_is_valid_after_restart()
    {
        const string login = "owner";
        const string password = "parolparol";
        string cookie;
        using (var first = StartServer())
        {
            using (var scope = first.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
                db.Users.Add(new UserRow
                {
                    Login = login, PasswordHash = PasswordHasher.Hash(password), Role = UserRole.Owner,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                db.SaveChanges();
            }

            var client = first.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false, HandleCookies = false,
            });
            var answer = await client.PostAsync("/login", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["login"] = login, ["password"] = password }));
            Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
            cookie = string.Join("; ", answer.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        }

        using var second = StartServer();
        var again = second.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/settings");
        request.Headers.Add("Cookie", cookie);

        var response = await again.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
