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

    [Theory]
    [InlineData("http://notes.example.com")]
    [InlineData("http://192.168.1.47:8080")]
    public void Plain_http_to_another_host_gets_a_warning(string url)
    {
        var warning = SamizdatClient.PlainHttpWarning(new Uri(url));

        Assert.NotNull(warning);
        Assert.Contains(new Uri(url).GetLeftPart(UriPartial.Authority), warning);
    }

    // Предупреждение видно на экране и в логах: логин и пароль из адреса туда попасть не должны.
    [Fact]
    public void Warning_does_not_show_the_user_and_password_from_the_address()
    {
        var warning = SamizdatClient.PlainHttpWarning(new Uri("http://user:pass@notes.example.com:8080/x"));

        Assert.NotNull(warning);
        Assert.Contains("http://notes.example.com:8080", warning);
        Assert.DoesNotContain("user", warning);
        Assert.DoesNotContain("pass", warning);
    }

    [Theory]
    [InlineData("https://notes.example.com")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://LocalHost:5080")]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://127.10.20.30")]
    [InlineData("http://[::1]:5080")]
    public void Https_and_loopback_get_no_warning(string url)
    {
        Assert.Null(SamizdatClient.PlainHttpWarning(new Uri(url)));
    }

    [Fact]
    public void Client_from_config_writes_the_warning()
    {
        var warnings = new StringWriter();

        SamizdatClient.FromConfig(new CliConfig { ServerUrl = "http://notes.example.com", Token = "t" }, warnings);

        Assert.Contains("http://notes.example.com", warnings.ToString());
    }

    [Fact]
    public void Client_from_config_is_silent_for_https()
    {
        var warnings = new StringWriter();

        SamizdatClient.FromConfig(new CliConfig { ServerUrl = "https://notes.example.com", Token = "t" }, warnings);

        Assert.Equal("", warnings.ToString());
    }
}
