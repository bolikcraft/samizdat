using Markdig;
using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

/// ArticleIndexer разбирает markdown один раз и переиспользует MarkdownDocument для
/// PlainText.Extract и WikiLinks.Targets — эти тесты проверяют, что перегрузка от
/// готового документа даёт тот же результат, что и перегрузка от строки.
public class IndexDocumentReuseTests
{
    const string Markdown = "> [!note] Заголовок\n> см. [[proxmox]] и текст\n";

    [Fact]
    public void PlainText_from_a_parsed_document_matches_plain_text_from_the_string()
    {
        var fromString = PlainText.Extract(Markdown);

        var document = Markdig.Markdown.Parse(Markdown, IndexPipeline.Instance);
        var fromDocument = PlainText.Extract(document);

        Assert.Equal(fromString, fromDocument);
    }

    [Fact]
    public void WikiLinks_from_a_parsed_document_matches_targets_from_the_string()
    {
        var fromString = WikiLinks.Targets(Markdown);

        var document = Markdig.Markdown.Parse(Markdown, IndexPipeline.Instance);
        var fromDocument = WikiLinks.Targets(document);

        Assert.Equal(fromString, fromDocument);
    }

    [Fact]
    public void Reusing_one_document_for_links_then_text_matches_two_independent_parses()
    {
        // Порядок как в ArticleIndexer.Index: сначала ссылки на нетронутом дереве,
        // потом текст — Extract сам применит CalloutTransformer поверх того же объекта.
        var document = Markdig.Markdown.Parse(Markdown, IndexPipeline.Instance);
        var links = WikiLinks.Targets(document);
        var text = PlainText.Extract(document);

        Assert.Equal(WikiLinks.Targets(Markdown), links);
        Assert.Equal(PlainText.Extract(Markdown), text);
    }
}
