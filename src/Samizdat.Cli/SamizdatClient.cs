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

    public async Task<Dictionary<string, string>> GetStateAsync()
        => await http.GetFromJsonAsync<Dictionary<string, string>>("/api/state") ?? [];

    public async Task PutArticleAsync(string slug, byte[] markdown,
                                      IReadOnlyCollection<(string Name, byte[] Bytes)> attachments, string folder)
    {
        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(markdown), "index.md", "index.md" },
            { new StringContent(folder, Encoding.UTF8), "folder" },
        };
        foreach (var (name, bytes) in attachments)
            content.Add(new ByteArrayContent(bytes), "attachments", name);

        var response = await http.PutAsync($"/api/articles/{Uri.EscapeDataString(slug)}", content);
        if (!response.IsSuccessStatusCode)
            throw new CliException($"{slug}: сервер ответил {(int)response.StatusCode} " +
                                   await response.Content.ReadAsStringAsync());
    }

    public async Task DeleteArticleAsync(string slug)
        => (await http.DeleteAsync($"/api/articles/{Uri.EscapeDataString(slug)}")).EnsureSuccessStatusCode();

    public async Task<string> GetMarkdownAsync(string slug)
        => await http.GetStringAsync($"/api/articles/{Uri.EscapeDataString(slug)}.md");
}

public sealed class CliException(string message) : Exception(message);
