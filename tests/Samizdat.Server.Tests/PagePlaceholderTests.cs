using Samizdat.Server.Auth;
using Samizdat.Server.Rendering;

namespace Samizdat.Server.Tests;

public class PagePlaceholderTests
{
    [Fact]
    public void Marker_carries_the_random_part_of_this_process()
    {
        Assert.Matches("^[0-9A-F]{32}$", PagePlaceholder.Nonce);
        Assert.Equal($"__ANTIFORGERY_{PagePlaceholder.Nonce}__", AntiforgeryHtml.Placeholder);
        Assert.Equal($"__SHARE_PANEL_{PagePlaceholder.Nonce}__", SharePanel.Placeholder);
    }
}
