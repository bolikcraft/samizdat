using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Samizdat.Server.Tests;

public class PageEndpointsTests : IDisposable
{
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    HttpClient StartServer()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("Samizdat:DataRoot", dataRoot));
        return factory.CreateClient();
    }

    void WriteArticle(string slug, string text)
    {
        var folder = Path.Combine(dataRoot, "articles", slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.md"), text);
    }

    [Fact]
    public async Task Shows_article_page()
    {
        WriteArticle("privet", "---\ntitle: Привет\n---\n# Привет\n\nтекст\n");
        var client = StartServer();

        var html = await client.GetStringAsync("/privet");

        Assert.Contains("<h1>Привет</h1>", html);
        Assert.Contains("<!doctype html>", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_slug_returns_404_page()
    {
        var client = StartServer();

        var response = await client.GetAsync("/нет-такой");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Нет такой страницы", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Serves_attachment_from_article_folder()
    {
        WriteArticle("s", "---\ntitle: T\n---\n![[pic.png]]");
        File.WriteAllBytes(Path.Combine(dataRoot, "articles", "s", "pic.png"), [1, 2, 3]);
        var client = StartServer();

        var response = await client.GetAsync("/s/pic.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Path_outside_article_folder_is_refused()
    {
        var client = StartServer();

        var response = await client.GetAsync("/s/..%2f..%2fappsettings.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);
}
