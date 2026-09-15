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
    public static HttpClient ClientWithToken(WebApplicationFactory<Program> factory) =>
        ClientWithOwner(factory).Client;

    /// Логин и пароль нужны тем тестам, что открывают ещё и страницы сайта: их пускает cookie-сессия.
    public static (HttpClient Client, string Login, string Password) ClientWithOwner(
        WebApplicationFactory<Program> factory)
    {
        var login = $"owner-{Guid.NewGuid():N}";
        const string password = "x";

        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            var owner = new UserRow
            {
                Login = login,
                PasswordHash = PasswordHasher.Hash(password),
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
        return (client, login, password);
    }

    /// Форма выкладки. Поле folder тут не задано: тесты, которым важна папка, добавляют его сами.
    public static MultipartFormDataContent Form(string markdown, params (string Name, byte[] Bytes)[] attachments)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes(markdown)), "index.md", "index.md" },
        };
        foreach (var (name, bytes) in attachments)
            form.Add(new ByteArrayContent(bytes), "attachments", name);
        return form;
    }

    public static async Task<HttpResponseMessage> Push(HttpClient client, string slug, string markdown,
                                                       string folder = "")
    {
        using var form = Form(markdown);
        form.Add(new StringContent(folder), "folder");

        return await client.PutAsync($"/api/articles/{slug}", form);
    }
}
