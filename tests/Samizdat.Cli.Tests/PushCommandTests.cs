using System.Net;
using System.Net.Http.Json;
using Samizdat.Cli;

namespace Samizdat.Cli.Tests;

public class PushCommandTests : IDisposable
{
    readonly string vault = Directory.CreateTempSubdirectory("samizdat-vault").FullName;

    [Fact]
    public async Task Failed_delete_does_not_stop_the_other_deletes()
    {
        var server = new FakeServer(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Dictionary<string, string> { ["a"] = "h", ["b"] = "h" }),
                };

            return new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith("/a", StringComparison.Ordinal)
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.NoContent);
        });

        var code = await PushCommand.RunAsync(ArgParser.Parse(["push", "--prune"]), vault,
                                              new SamizdatClient(server.Client()));

        Assert.Equal(1, code);
        Assert.Contains("DELETE /api/articles/b", server.Requests);
    }

    public void Dispose() => Directory.Delete(vault, recursive: true);
}
