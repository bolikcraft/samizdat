using System.Reflection;
using Markdig;
using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

/// Рендер статьи и индекс парсят markdown двумя разными конвейерами Markdig — расширения должны
/// совпадать, иначе поиск увидит текст иначе, чем страница, и никто не заметит.
public class MarkdownPipelinesTests
{
    [Fact]
    public void Article_pipeline_has_the_same_extensions_as_the_index_pipeline_besides_highlighting()
    {
        // Поле приватное: читаем через рефлексию, чтобы сравнивать конвейер, который реально
        // строит ArticleRenderer, а не его копию, написанную заново в тесте.
        var field = typeof(ArticleRenderer).GetField("pipeline", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var articlePipeline = (MarkdownPipeline)field.GetValue(new ArticleRenderer())!;

        // ColorCodeExtension у Markdown.ColorCode internal, сравниваем по имени типа — это
        // единственное расхождение, которое тест обязан пропустить.
        var articleTypes = articlePipeline.Extensions.Select(extension => extension.GetType().Name)
            .Where(name => name != "ColorCodeExtension");
        var indexTypes = IndexPipeline.Instance.Extensions.Select(extension => extension.GetType().Name);

        Assert.Equal(indexTypes, articleTypes);
    }
}
