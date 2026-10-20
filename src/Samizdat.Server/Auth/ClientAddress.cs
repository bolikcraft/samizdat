using System.Net;
using System.Net.Sockets;

namespace Samizdat.Server.Auth;

/// Ключ лимита попыток по адресу клиента.
public static class ClientAddress
{
    // IPv6 — по сети /64: абонент обычно владеет ею целиком и иначе получал бы окно на каждый адрес.
    public static string LimitKey(IPAddress? address)
    {
        if (address is null) return "";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }
}
