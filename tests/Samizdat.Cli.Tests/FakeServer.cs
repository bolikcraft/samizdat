using System.Net;

namespace Samizdat.Cli.Tests;

sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    public HttpClient Client() => new(this) { BaseAddress = new Uri("http://samizdat.test") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        return Task.FromResult(respond(request));
    }
}

/// Не отвечает никогда: запрос завершает только тайм-аут HttpClient.
sealed class SilentServer : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
