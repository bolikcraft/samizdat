using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Samizdat.Server;

/// Прокси, чьим заголовкам X-Forwarded-For и X-Forwarded-Proto сервер верит. Настройка
/// Samizdat:Proxy: адреса IP и сети CIDR через запятую.
public static class TrustedProxies
{
    /// Разбирает настройку сразу: опечатка в ней останавливает запуск, а не всплывает на первом запросе.
    public static Action<ForwardedHeadersOptions> From(string? setting)
    {
        List<IPAddress> proxies = [];
        List<System.Net.IPNetwork> networks = [];
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            // TryParse читает «10.1» как 10.0.0.1: адрес IPv4 без четырёх чисел — опечатка, а не сокращение.
            var host = entry.Split('/')[0];
            var complete = host.Contains(':') || host.Count(c => c == '.') == 3;
            if (complete && entry.Contains('/') && System.Net.IPNetwork.TryParse(entry, out var network)) networks.Add(network);
            else if (complete && !entry.Contains('/') && IPAddress.TryParse(entry, out var address)) proxies.Add(address);
            // Молча пропущенная запись оставила бы всех клиентов с адресом прокси.
            else throw new InvalidOperationException($"Samizdat:Proxy: «{entry}» — не адрес IP и не сеть CIDR.");
        }

        return options =>
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            // При пустых списках middleware верит всем, поэтому без прокси заголовки выключены целиком.
            if (proxies.Count == 0 && networks.Count == 0)
            {
                options.ForwardedHeaders = ForwardedHeaders.None;
                return;
            }

            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in proxies) options.KnownProxies.Add(proxy);
            foreach (var range in networks) options.KnownIPNetworks.Add(range);
        };
    }
}
