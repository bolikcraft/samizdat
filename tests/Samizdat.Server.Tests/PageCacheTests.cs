using Samizdat.Server.Rendering;

namespace Samizdat.Server.Tests;

public class PageCacheTests
{
    static PageKey Key(string content = "hash1", string theme = "theme1",
                       string catalog = "cat1", string view = "view1")
        => new(content, theme, catalog, view);

    [Fact]
    public void Second_call_with_same_key_does_not_build_page()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", Key(), () => { builds++; return "html"; });
        cache.GetOrBuild("s", Key(), () => { builds++; return "html"; });

        Assert.Equal(1, builds);
    }

    [Fact]
    public void New_content_hash_builds_again()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", Key(), () => { builds++; return "a"; });
        var second = cache.GetOrBuild("s", Key(content: "hash2"), () => { builds++; return "b"; });

        Assert.Equal(2, builds);
        Assert.Equal("b", second);
    }

    [Fact]
    public void New_theme_version_builds_again()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", Key(), () => { builds++; return "a"; });
        var second = cache.GetOrBuild("s", Key(theme: "theme2"), () => { builds++; return "b"; });

        Assert.Equal(2, builds);
        Assert.Equal("b", second);
    }

    [Fact]
    public void New_catalog_fingerprint_builds_again()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", Key(), () => { builds++; return "a"; });
        var second = cache.GetOrBuild("s", Key(catalog: "cat2"), () => { builds++; return "b"; });

        Assert.Equal(2, builds);
        Assert.Equal("b", second);
    }

    [Fact]
    public void New_view_fingerprint_builds_again()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", Key(), () => { builds++; return "a"; });
        var second = cache.GetOrBuild("s", Key(view: "view2"), () => { builds++; return "b"; });

        Assert.Equal(2, builds);
        Assert.Equal("b", second);
    }

    /// Со склейкой полей в строку через "|" эти два ключа совпали бы. Разделитель внутри поля —
    /// не выдумка: IThemeSource.Version сам склеен из версий слоёв тем же способом.
    [Fact]
    public void Two_keys_that_differ_only_by_where_the_separator_falls_build_twice()
    {
        var cache = new PageCache();
        var builds = 0;

        cache.GetOrBuild("s", Key(theme: "disk|embedded", catalog: "cat"),
                         () => { builds++; return "a"; });
        var second = cache.GetOrBuild("s", Key(theme: "disk", catalog: "embedded|cat"),
                                      () => { builds++; return "b"; });

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
            results[i] = cache.GetOrBuild("s", Key(), () => "html");
        });

        Assert.All(results, html => Assert.Equal("html", html));
    }
}
