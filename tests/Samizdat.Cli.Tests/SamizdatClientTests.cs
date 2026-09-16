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
}
