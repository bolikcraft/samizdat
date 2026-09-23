using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class HtmlCleaningTests
{
    readonly ArticleRenderer renderer = new();

    string Render(string markdown) => renderer.Render(markdown, "s", NoArticles.Instance);

    [Fact]
    public void Script_tag_is_removed_with_its_code()
    {
        var html = Render("<script>alert(1)</script>\n\nтекст");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", html);
        Assert.Contains("текст", html);
    }

    [Fact]
    public void Script_inside_svg_is_removed()
    {
        var html = Render("<svg><script>alert(1)</script></svg>");

        Assert.DoesNotContain("alert", html);
    }

    [Fact]
    public void Drawn_diagram_stays_whole()
    {
        var html = Render(
            "<svg style=\"width:100%;height:auto\" viewBox=\"0 0 200 100\" role=\"img\" aria-label=\"Схема\">"
            + "<defs><marker id=\"a1\" markerWidth=\"9\" markerHeight=\"9\" refX=\"8\" refY=\"3\" orient=\"auto\">"
            + "<path d=\"M0,0 L8,3 L0,6 z\" fill=\"var(--text-faint)\"/></marker></defs>"
            + "<rect x=\"4\" y=\"4\" width=\"80\" height=\"40\" rx=\"9\" stroke-width=\"1.5\"/>"
            + "<path d=\"M90,24 L140,24\" marker-end=\"url(#a1)\"/>"
            + "<text x=\"44\" y=\"28\" font-size=\"14\" text-anchor=\"middle\">Браузер</text></svg>");

        Assert.Contains("<svg style=\"width: 100%; height: auto\" viewBox=\"0 0 200 100\" role=\"img\"", html);
        Assert.Contains("<marker id=\"a1\" markerWidth=\"9\" markerHeight=\"9\" refX=\"8\" refY=\"3\" orient=\"auto\">", html);
        Assert.Contains("<path d=\"M0,0 L8,3 L0,6 z\" fill=\"var(--text-faint)\">", html);
        Assert.Contains("rx=\"9\"", html);
        Assert.Contains("marker-end=\"url(#a1)\"", html);
        Assert.Contains("<text x=\"44\" y=\"28\" font-size=\"14\" text-anchor=\"middle\">Браузер</text>", html);
        Assert.Contains("aria-label=\"Схема\"", html);
    }

    [Theory]
    [InlineData("<svg><foreignObject><img src=\"x\" onerror=\"alert(1)\"></foreignObject></svg>", "foreignObject")]
    [InlineData("<svg><use href=\"https://evil.example/x.svg#a\"></use></svg>", "use")]
    [InlineData("<svg><a href=\"#x\"><animate attributeName=\"href\" to=\"javascript:alert(1)\"></animate></a></svg>", "animate")]
    public void Dangerous_svg_tag_is_removed(string markdown, string tag)
    {
        var html = Render(markdown);

        Assert.DoesNotContain("<" + tag, html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public void Event_handler_inside_svg_is_removed()
    {
        var html = Render("<svg><rect width=\"10\" height=\"10\" onclick=\"alert(1)\"/></svg>");

        Assert.Contains("<rect width=\"10\" height=\"10\">", html);
        Assert.DoesNotContain("onclick", html);
    }

    [Fact]
    public void Event_handler_attribute_is_removed_but_the_tag_stays()
    {
        var html = Render("до <img src=\"pic.png\" onerror=\"alert(1)\"> после");

        Assert.Contains("<img src=\"pic.png\">", html);
        Assert.DoesNotContain("onerror", html);
    }

    [Fact]
    public void Generic_attributes_cannot_add_an_event_handler()
    {
        var html = Render("# Заголовок {onclick=\"alert(1)\"}");

        Assert.Contains("Заголовок</h1>", html);
        Assert.DoesNotContain("onclick", html);
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<a href=\"java&#09;script:alert(1)\">x</a>")]
    [InlineData("[x](data:text/html,<b>x</b>)")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>")]
    public void Link_with_a_dangerous_scheme_loses_its_address(string markdown)
    {
        var html = Render(markdown);

        Assert.Contains("<a>x</a>", html);
        Assert.DoesNotContain("href", html);
    }

    [Theory]
    [InlineData("[x](https://example.com/a)", "href=\"https://example.com/a\"")]
    [InlineData("[x](http://example.com/a)", "href=\"http://example.com/a\"")]
    [InlineData("[x](mailto:me@example.com)", "href=\"mailto:me@example.com\"")]
    [InlineData("[x](/other)", "href=\"/other\"")]
    [InlineData("[x](other/page)", "href=\"other/page\"")]
    [InlineData("[x](#razdel)", "href=\"#razdel\"")]
    public void Safe_address_stays(string markdown, string expected)
        => Assert.Contains(expected, Render(markdown));

    [Fact]
    public void Html_tags_used_in_obsidian_notes_stay()
    {
        var html = Render("<details><summary>Итог</summary>скрыто</details>\n\n"
                          + "H<sub>2</sub>O, x<sup>2</sup>, <mark>метка</mark><br><kbd>Ctrl</kbd> <u>черта</u>");

        Assert.Contains("<details><summary>Итог</summary>скрыто</details>", html);
        Assert.Contains("H<sub>2</sub>O, x<sup>2</sup>, <mark>метка</mark><br><kbd>Ctrl</kbd> <u>черта</u>", html);
    }

    [Fact]
    public void Unknown_tag_is_removed_but_its_text_stays()
    {
        var html = Render("<center>по центру</center>");

        Assert.DoesNotContain("<center", html);
        Assert.Contains("по центру", html);
    }

    [Fact]
    public void Form_and_style_blocks_are_removed()
    {
        var html = Render("<style>body{display:none}</style><form action=\"https://evil.example\"><button>ок</button></form>");

        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("display:none", html);
        Assert.DoesNotContain("<form", html);
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public void Youtube_embed_keeps_its_https_frame()
    {
        var html = Render("![](https://www.youtube.com/watch?v=dQw4w9WgXcQ)");

        Assert.Contains("<iframe src=\"https://www.youtube.com/embed/dQw4w9WgXcQ\"", html);
    }

    [Theory]
    [InlineData("<iframe src=\"/settings\"></iframe>")]
    [InlineData("<iframe src=\"http://example.com\"></iframe>")]
    [InlineData("<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>")]
    public void Frame_without_an_https_address_gets_no_content(string markdown)
    {
        var html = Render(markdown);

        Assert.Contains("<iframe></iframe>", html);
    }

    [Fact]
    public void Task_list_footnotes_and_code_highlight_survive_cleaning()
    {
        var html = Render("- [x] готово\n\nтекст[^1]\n\n[^1]: примечание\n\n```csharp\nvar x = 1;\n```");

        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("href=\"#fn:1\"", html);
        Assert.Contains("href=\"#fnref:1\"", html);
        Assert.Contains("<span style=\"color:", html);
    }
}
