using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

/// Готовый вход в тестах: заводит учётку и возвращает клиент с cookie-сессией.
public static class TestLogin
{
    public static Task<HttpClient> AsOwner(WebApplicationFactory<Program> factory) => As(factory, UserRole.Owner);

    public static Task<HttpClient> AsReader(WebApplicationFactory<Program> factory) => As(factory, UserRole.Reader);

    public static async Task<HttpClient> As(WebApplicationFactory<Program> factory, UserRole role,
                                            string? login = null)
    {
        const string password = "parol";
        login ??= $"{role}-{Guid.NewGuid():N}";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
            db.Users.Add(new UserRow
            {
                Login = login, PasswordHash = PasswordHasher.Hash(password), Role = role,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }

        // Без автоперехода: иначе клиент сам сходит по редиректу и тест не увидит его кода.
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var answer = await PostLogin(client, login, password);
        if (answer.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Вход не удался: {answer.StatusCode}");
        return client;
    }

    /// Вход через форму, как в браузере: сначала страница входа ради antiforgery-поля и cookie.
    // ConfigureAwait(false): часть тестов ждёт этот вызов через .Wait().
    public static async Task<HttpResponseMessage> PostLogin(HttpClient client, string login, string password)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/login").ConfigureAwait(false));
        return await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = login, ["password"] = password, [name] = value,
        })).ConfigureAwait(false);
    }

    // Форма достаёт свой antiforgery-токен со страницы — сервер требует его на каждом небезопасном POST.
    public static async Task<HttpResponseMessage> Post(HttpClient client, string path, Dictionary<string, string> fields)
    {
        var (name, value) = AntiforgeryToken(await client.GetStringAsync("/"));
        fields[name] = value;
        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    public static (string Name, string Value) AntiforgeryToken(string html)
    {
        var match = Regex.Match(html, "<input type=\"hidden\" name=\"([^\"]+)\" value=\"([^\"]+)\">");
        if (!match.Success) throw new InvalidOperationException("Antiforgery-поле не найдено на странице");
        return (match.Groups[1].Value, match.Groups[2].Value);
    }
}
