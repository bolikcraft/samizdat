using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class MermaidBlockTests
{
    readonly ArticleRenderer renderer = new();

    string Render(string markdown, bool diagrams) => renderer.Render(markdown, "s", NoArticles.Instance, diagrams: diagrams);

    [Fact]
    public void With_diagrams_on_a_mermaid_block_becomes_pre_mermaid_with_escaped_text()
    {
        var html = Render("```mermaid\nflowchart LR\n  A[\"a < b & c\"] --> B\n```", diagrams: true);

        Assert.Contains("<pre class=\"mermaid\">flowchart LR\n  A[\"a &lt; b &amp; c\"] --&gt; B\n</pre>", html);
        // Подсветки ColorCode у схемы нет.
        Assert.DoesNotContain("<span", html);
        Assert.DoesNotContain("<code", html);
    }

    [Fact]
    public void With_diagrams_off_a_mermaid_block_is_plain_code()
    {
        var html = Render("```mermaid\nflowchart LR\n  A --> B\n```", diagrams: false);

        Assert.DoesNotContain("class=\"mermaid\"", html);
        Assert.Contains("<pre><code class=\"language-mermaid\">flowchart LR\n  A --&gt; B\n</code></pre>", html);
    }

    [Fact]
    public void The_word_mermaid_is_matched_in_any_case()
        => Assert.Contains("<pre class=\"mermaid\">flowchart LR", Render("```Mermaid\nflowchart LR\n```", diagrams: true));

    [Fact]
    public void Other_code_blocks_keep_their_look_with_diagrams_on()
    {
        var html = Render("```csharp\nvar x = 1;\n```\n\n```foo\nplain\n```", diagrams: true);

        Assert.Contains("<span", html);
        Assert.Contains("<pre><code class=\"language-foo\">plain", html);
    }

    [Fact]
    public void A_math_block_keeps_its_own_renderer()
        => Assert.Contains("<div class=\"math\">", Render("$$\nx^2\n$$", diagrams: true));

    [Fact]
    public void The_search_index_keeps_the_text_of_a_diagram()
        => Assert.Contains("flowchart LR", PlainText.Extract("```mermaid\nflowchart LR\n  A --> B\n```"));
}
