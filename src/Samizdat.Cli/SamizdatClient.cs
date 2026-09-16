using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Samizdat.Cli;

public sealed class SamizdatClient(HttpClient http)
{
    public static SamizdatClient FromConfig(CliConfig config)
    {
        var http = new HttpClient { BaseAddress = new Uri(config.ServerUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        return new SamizdatClient(http);
    }

    public Task<Dictionary<string, string>> GetStateAsync()
        => WithTimeout("/api/state",
                       async () => await http.GetFromJsonAsync<Dictionary<string, string>>("/api/state") ?? []);

    public async Task PutArticleAsync(string slug, byte[] markdown,
                                      IReadOnlyCollection<(string Name, byte[] Bytes)> attachments, string folder,
                                      string name)
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(markdown), "index.md", "index.md" },
            { new StringContent(folder, Encoding.UTF8), "folder" },
            { new StringContent(name, Encoding.UTF8), "name" },
        };
        foreach (var (attachment, bytes) in attachments)
            content.Add(new ByteArrayContent(bytes), "attachments", attachment);

        using var response = await WithTimeout(
            slug, () => http.PutAsync($"/api/articles/{Uri.EscapeDataString(slug)}", content));
        await EnsureSuccessAsync(slug, response);
    }

    public async Task DeleteArticleAsync(string slug)
    {
        using var response = await WithTimeout(
            slug, () => http.DeleteAsync($"/api/articles/{Uri.EscapeDataString(slug)}"));
        await EnsureSuccessAsync(slug, response);
    }

    public Task<string> GetMarkdownAsync(string slug)
        => WithTimeout(slug, () => http.GetStringAsync($"/api/articles/{Uri.EscapeDataString(slug)}.md"));

    static async Task EnsureSuccessAsync(string slug, HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new CliException($"{slug}: сервер ответил {(int)response.StatusCode} " +
                                   await response.Content.ReadAsStringAsync());
    }

    /// По тайм-ауту HttpClient бросает TaskCanceledException с TimeoutException внутри.
    /// Отмены по другой причине в CLI нет, но их не маскируем.
    async Task<T> WithTimeout<T>(string what, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (TaskCanceledException error) when (error.InnerException is TimeoutException)
        {
            throw new CliException($"{what}: сервер не ответил за {http.Timeout.TotalSeconds:0} с");
        }
    }
}

public sealed class CliException(string message) : Exception(message);
