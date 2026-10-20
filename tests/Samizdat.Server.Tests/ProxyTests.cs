using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Samizdat.Server.Auth;
using Samizdat.Server.Data;

namespace Samizdat.Server.Tests;

[Collection("db")]
public class ProxyTests : IDisposable
{
    readonly DatabaseFixture database;
    readonly string dataRoot = Directory.CreateTempSubdirectory("samizdat-data").FullName;

    public ProxyTests(DatabaseFixture database)
    {
        this.database = database;
        database.ResetDatabase();
    }

    public void Dispose() => Directory.Delete(dataRoot, recursive: true);

    // Каждый тест поднимает один сервер с владельцем aleks.
    WebApplicationFactory<Program> StartServer(params (string Key, string Value)[] settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Samizdat:DataRoot", dataRoot);
            builder.UseSetting("ConnectionStrings:Postgres", database.ConnectionString);
            // Слежение за файлом настроек тут не нужно: тесты поднимают десятки хостов,
            // и наблюдатели inotify упираются в системный лимит.
            builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, TestRemoteAddress>());
        });

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SamizdatDbContext>();
        db.Users.Add(new UserRow
        {
            Login = "aleks", PasswordHash = PasswordHasher.Hash("parol-aleksa"), Role = UserRole.Owner,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return factory;
    }

    static HttpClient ClientFrom(WebApplicationFactory<Program> factory, string remote, string? forwardedFor = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestRemoteAddress.Header, remote);
        if (forwardedFor is not null) client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return client;
    }

    // Клиент без хранилища cookie: cookie с Secure обычный клиент не вернул бы серверу по http://localhost.
    static async Task<bool> SessionCookieIsSecure(WebApplicationFactory<Program> factory, string remote,
                                                  string forwardedProto)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false, HandleCookies = false,
        });
        client.DefaultRequestHeaders.Add(TestRemoteAddress.Header, remote);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", forwardedProto);

        var page = await client.GetAsync("/login");
        var (name, value) = TestLogin.AntiforgeryToken(await page.Content.ReadAsStringAsync());
        using var signIn = new HttpRequestMessage(HttpMethod.Post, "/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["login"] = "aleks", ["password"] = "parol-aleksa", [name] = value,
            }),
        };
        signIn.Headers.Add("Cookie", string.Join("; ",
            page.Headers.GetValues("Set-Cookie").Select(header => header.Split(';')[0])));
        var answer = await client.SendAsync(signIn);

        Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
        return answer.Headers.GetValues("Set-Cookie")
            .Any(cookie => cookie.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal)
                           && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Trusted_proxy_sets_the_https_scheme()
    {
        var factory = StartServer(("Samizdat:Proxy", "10.0.0.1"));

        Assert.True(await SessionCookieIsSecure(factory, remote: "10.0.0.1", forwardedProto: "https"));
    }

    [Fact]
    public async Task Other_sender_does_not_set_the_https_scheme()
    {
        var factory = StartServer(("Samizdat:Proxy", "10.0.0.1"));

        Assert.False(await SessionCookieIsSecure(factory, remote: "10.0.0.2", forwardedProto: "https"));
    }

    [Fact]
    public async Task Without_the_setting_nobody_sets_the_https_scheme()
    {
        var factory = StartServer();

        Assert.False(await SessionCookieIsSecure(factory, remote: "10.0.0.1", forwardedProto: "https"));
    }

    [Fact]
    public async Task Proxy_network_in_cidr_form_is_trusted()
    {
        var factory = StartServer(("Samizdat:Proxy", "10.0.0.1, 192.168.1.0/24"));

        Assert.True(await SessionCookieIsSecure(factory, remote: "192.168.1.47", forwardedProto: "https"));
    }

    [Fact]
    public async Task Limit_counts_the_client_behind_the_proxy()
    {
        var factory = StartServer(("Samizdat:Proxy", "10.0.0.1"), ("Samizdat:Auth:AttemptsPerMinute", "1"));

        var first = await TestLogin.PostLogin(ClientFrom(factory, "10.0.0.1", "203.0.113.1"), "aleks", "mimo-mimo");
        var neighbour = await TestLogin.PostLogin(ClientFrom(factory, "10.0.0.1", "203.0.113.2"), "aleks", "mimo-mimo");
        var again = await TestLogin.PostLogin(ClientFrom(factory, "10.0.0.1", "203.0.113.1"), "aleks", "mimo-mimo");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, neighbour.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, again.StatusCode);
    }

    // Чужой X-Forwarded-For не дробит окно: иначе каждая попытка шла бы со своим «адресом».
    [Fact]
    public async Task Forwarded_address_from_another_sender_does_not_split_the_limit()
    {
        var factory = StartServer(("Samizdat:Proxy", "10.0.0.1"), ("Samizdat:Auth:AttemptsPerMinute", "1"));

        var first = await TestLogin.PostLogin(ClientFrom(factory, "10.0.0.9", "203.0.113.1"), "aleks", "mimo-mimo");
        var second = await TestLogin.PostLogin(ClientFrom(factory, "10.0.0.9", "203.0.113.2"), "aleks", "mimo-mimo");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    // Прокси дописывает адрес клиента в конец. Сервер берёт только этот адрес, даже если он из сети прокси:
    // иначе клиент из этой сети подставил бы перед ним любой адрес и обошёл лимит.
    [Fact]
    public async Task Only_the_last_forwarded_address_counts()
    {
        var factory = StartServer(("Samizdat:Proxy", "10.0.0.0/24"), ("Samizdat:Auth:AttemptsPerMinute", "1"));

        var first = await TestLogin.PostLogin(ClientFrom(factory, "10.0.0.1", "203.0.113.1, 10.0.0.5"), "aleks", "mimo-mimo");
        var second = await TestLogin.PostLogin(ClientFrom(factory, "10.0.0.1", "203.0.113.2, 10.0.0.5"), "aleks", "mimo-mimo");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public void Setting_takes_addresses_and_networks()
    {
        var options = new ForwardedHeadersOptions();

        TrustedProxies.From("10.0.0.1, 192.168.1.0/24")(options);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), Assert.Single(options.KnownProxies));
        Assert.Equal(System.Net.IPNetwork.Parse("192.168.1.0/24"), Assert.Single(options.KnownIPNetworks));
    }

    [Fact]
    public void Empty_setting_turns_the_headers_off()
    {
        var options = new ForwardedHeadersOptions();

        TrustedProxies.From("")(options);

        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
        Assert.Empty(options.KnownProxies);
        Assert.Empty(options.KnownIPNetworks);
    }

    [Theory]
    [InlineData("10.0.0.1, ne-adres")]
    [InlineData("10.0.0.0/40")]
    [InlineData("192.168.147")]
    [InlineData("10.1")]
    [InlineData("192.168.1/24")]
    public void Bad_entry_stops_the_server(string setting)
        => Assert.Throws<InvalidOperationException>(() => TrustedProxies.From(setting));
}
