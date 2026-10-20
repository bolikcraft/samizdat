using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Samizdat.Server.Tests;

/// TestServer не знает адреса клиента: RemoteIpAddress у запроса пуст, а с пустым адресом
/// ForwardedHeadersMiddleware принимает заголовок без проверки. Фильтр ставит адрес из заголовка
/// теста раньше всех middleware приложения, как это делает Kestrel.
public sealed class TestRemoteAddress : IStartupFilter
{
    public const string Header = "X-Test-Remote-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var value))
                context.Connection.RemoteIpAddress = IPAddress.Parse(value.ToString());
            return nextMiddleware(context);
        });
        next(app);
    };
}
