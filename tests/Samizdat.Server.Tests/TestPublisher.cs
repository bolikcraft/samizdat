using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

/// Выкладка статьи через /api так, как это делает CLI: на ней же строится индекс.
public static class TestPublisher
{
    public static HttpClient ClientWithToken(WebApplicationFactory<Program> factory)
    {
        string token;
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

            token = ApiToken.Create();
            db.ApiTokens.Add(new ApiTokenRow
            {
                UserId = owner.Id, TokenHash = ApiToken.HashOf(token), CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<HttpResponseMessage> Push(HttpClient client, string slug, string markdown,
                                                       string folder = "")
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(folder), "folder" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(markdown)), "index.md", "index.md" },
        };

        return await client.PutAsync($"/api/articles/{slug}", form);
    }
}
