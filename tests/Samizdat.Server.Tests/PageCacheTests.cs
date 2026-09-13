using Samizdat.Server.Rendering;

namespace Samizdat.Server.Tests;

public class PageCacheTests
{
    [Fact]
    public void Second_call_with_same_key_does_not_build_page()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", "hash1", "theme1", "cat1", () => { builds++; return "html"; });
        cache.GetOrBuild("s", "hash1", "theme1", "cat1", () => { builds++; return "html"; });

        Assert.Equal(1, builds);
    }

    [Fact]
    public void New_content_hash_builds_again()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", "hash1", "theme1", "cat1", () => { builds++; return "a"; });
        var second = cache.GetOrBuild("s", "hash2", "theme1", "cat1", () => { builds++; return "b"; });

        Assert.Equal(2, builds);
        Assert.Equal("b", second);
    }

    [Fact]
    public void New_theme_version_builds_again()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", "h", "theme1", "cat1", () => { builds++; return "a"; });
        cache.GetOrBuild("s", "h", "theme2", "cat1", () => { builds++; return "b"; });

        Assert.Equal(2, builds);
    }

    [Fact]
    public void New_catalog_fingerprint_builds_again()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", "h", "theme1", "cat1", () => { builds++; return "a"; });
        var second = cache.GetOrBuild("s", "h", "theme1", "cat2", () => { builds++; return "b"; });

        Assert.Equal(2, builds);
        Assert.Equal("b", second);
    }

    [Fact]
    public void Concurrent_calls_do_not_throw_and_return_consistent_html()
    {
        var cache = new PageCache();

        var results = new string[200];
        Parallel.For(0, results.Length, i =>
        {
            results[i] = cache.GetOrBuild("s", "hash1", "theme1", "cat1", () => "html");
        });

        Assert.All(results, html => Assert.Equal("html", html));
    }
}
