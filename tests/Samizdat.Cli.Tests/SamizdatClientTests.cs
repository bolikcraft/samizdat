using System.Net;
using Samizdat.Cli;

namespace Samizdat.Cli.Tests;

public class SamizdatClientTests
{
    sealed class Recorder : HttpMessageHandler
    {
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                                     CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Put_sends_the_note_name()
    {
        var recorder = new Recorder();
        var client = new SamizdatClient(new HttpClient(recorder) { BaseAddress = new Uri("http://samizdat.test") });

        await client.PutArticleAsync("kak-nastroit-server", "текст"u8.ToArray(), [], "", "Моя заметка");

        Assert.Matches("form-data; name=\"?name\"?", recorder.Body);
        Assert.Contains("Моя заметка", recorder.Body);
    }

    [Fact]
    public async Task Failed_delete_reports_the_slug_and_the_status()
    {
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("сломалось"),
        });

        var error = await Assert.ThrowsAsync<CliException>(
            () => new SamizdatClient(server.Client()).DeleteArticleAsync("a"));

        Assert.Contains("a: сервер ответил 500", error.Message);
    }

    [Fact]
    public async Task Timeout_is_reported_as_a_clear_error()
    {
        var http = new HttpClient(new SilentServer())
        {
            BaseAddress = new Uri("http://samizdat.test"),
            Timeout = TimeSpan.FromMilliseconds(100),
        };

        var error = await Assert.ThrowsAsync<CliException>(
            () => new SamizdatClient(http).PutArticleAsync("a", [1], [], "", ""));

        Assert.Contains("a: сервер не ответил", error.Message);
    }
}
