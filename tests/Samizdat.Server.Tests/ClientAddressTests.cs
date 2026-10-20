using System.Net;
using Samizdat.Server.Auth;

namespace Samizdat.Server.Tests;

public class ClientAddressTests
{
    [Fact]
    public void IPv4_address_is_its_own_key()
        => Assert.Equal("203.0.113.5", ClientAddress.LimitKey(IPAddress.Parse("203.0.113.5")));

    [Fact]
    public void IPv4_address_inside_IPv6_is_read_as_IPv4()
        => Assert.Equal("203.0.113.5", ClientAddress.LimitKey(IPAddress.Parse("::ffff:203.0.113.5")));

    [Fact]
    public void Addresses_of_one_IPv6_network_share_a_key()
    {
        var first = ClientAddress.LimitKey(IPAddress.Parse("2001:db8:1:2:aaaa::1"));
        var second = ClientAddress.LimitKey(IPAddress.Parse("2001:db8:1:2:bbbb::2"));

        Assert.Equal("2001:db8:1:2::/64", first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Neighbour_IPv6_network_has_its_own_key()
        => Assert.NotEqual(ClientAddress.LimitKey(IPAddress.Parse("2001:db8:1:2::1")),
                           ClientAddress.LimitKey(IPAddress.Parse("2001:db8:1:3::1")));

    [Fact]
    public void Unknown_address_gives_an_empty_key()
        => Assert.Equal("", ClientAddress.LimitKey(null));
}
