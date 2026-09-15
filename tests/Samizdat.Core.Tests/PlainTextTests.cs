using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

public class PlainTextTests
{
    [Fact]
    public void Headings_and_lists_keep_their_words()
    {
        var text = PlainText.Extract("# Заголовок\n\n- первый\n- второй\n");

        Assert.Contains("Заголовок", text);
        Assert.Contains("первый", text);
        Assert.Contains("второй", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("<h1>", text);
    }

    [Fact]
    public void Code_block_keeps_its_text()
    {
        var text = PlainText.Extract("```csharp\nvar server = new Server();\n```");

        Assert.Contains("server", text);
        Assert.DoesNotContain("<pre>", text);
    }

    [Fact]
    public void Table_keeps_its_cells()
    {
        var text = PlainText.Extract("| город | страна |\n|---|---|\n| Тверь | Россия |");

        Assert.Contains("Тверь", text);
        Assert.Contains("Россия", text);
    }

    [Fact]
    public void Wiki_link_goes_in_by_its_label()
    {
        var text = PlainText.Extract("см. [[zametka|мой сервер]]");

        Assert.Contains("мой сервер", text);
        Assert.DoesNotContain("[[", text);
    }

    [Fact]
    public void Wiki_link_without_a_label_goes_in_by_its_target()
    {
        var text = PlainText.Extract("см. [[Заметка про Proxmox]]");

        Assert.Contains("Заметка про Proxmox", text);
    }

    [Fact]
    public void Picture_name_does_not_go_in()
    {
        var text = PlainText.Extract("![[shema.png]] текст");

        Assert.DoesNotContain("shema", text);
        Assert.Contains("текст", text);
    }

    [Fact]
    public void Raw_html_does_not_go_in()
    {
        var text = PlainText.Extract("абзац <b>жирный</b> дальше");

        Assert.DoesNotContain("<b>", text);
        Assert.Contains("жирный", text);
    }

    [Fact]
    public void Callout_without_a_title_gives_only_its_text()
    {
        var text = PlainText.Extract("> [!warning]\n> текст без заголовка\n");

        Assert.Contains("текст без заголовка", text);
        Assert.DoesNotContain("<svg", text);
        Assert.DoesNotContain("markdown-alert", text);
        Assert.DoesNotContain("Warning", text);
    }

    [Fact]
    public void Callout_with_a_title_gives_the_title_and_the_text()
    {
        var text = PlainText.Extract("> [!note] Про сервер\n> текст коллаута\n");

        Assert.Contains("Про сервер", text);
        Assert.Contains("текст коллаута", text);
        Assert.DoesNotContain("[!note]", text);
    }

    [Fact]
    public void Link_text_goes_in_but_the_address_does_not()
    {
        var text = PlainText.Extract("[мой сайт](https://example.com/tayna)");

        Assert.Contains("мой сайт", text);
        Assert.DoesNotContain("example.com", text);
    }
}
