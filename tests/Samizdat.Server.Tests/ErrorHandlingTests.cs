using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ErrorHandlingTests(DatabaseFixture database) : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    (WebApplicationFactory<Program> Factory, HttpClient Client) StartWithToken()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var owner = new UserRow
            {
                Login = $"owner-{Guid.NewGuid():N}",
                PasswordHash = PasswordHasher.Hash("x"),
                Role = UserRole.Owner,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(owner);
            db.SaveChanges();

            var token = ApiToken.Create();
            db.ApiTokens.Add(new ApiTokenRow
            {
                UserId = owner.Id,
                TokenHash = ApiToken.HashOf(token),
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();

            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return (factory, client);
        }
    }

    static MultipartFormDataContent Article(string markdown)
        => new() { { new ByteArrayContent(Encoding.UTF8.GetBytes(markdown)), "index.md", "index.md" } };

    // Права доступа Unix: тест воспроизводим только на Linux/macOS, как и Testcontainers-стенд в CI.
    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Put_failing_because_of_unwritable_articles_directory_returns_500_without_details()
    {
        var (_, client) = StartWithToken();
        var articlesRoot = Path.Combine(dataRoot, "articles");
        Directory.CreateDirectory(articlesRoot);
        // Каталог существует, но недоступен для записи: Directory.CreateDirectory внутри него бросит
        // UnauthorizedAccessException с абсолютным путём — именно это не должно уйти клиенту.
        File.SetUnixFileMode(articlesRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            var response = await client.PutAsync("/api/articles/st", Article("текст"));
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("UnauthorizedAccessException", body);
            Assert.DoesNotContain(dataRoot, body);
            Assert.DoesNotContain(articlesRoot, body);
        }
        finally
        {
            File.SetUnixFileMode(articlesRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
