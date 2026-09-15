using Samizdat.Core.Rendering;

namespace Samizdat.Core.Tests;

/// Рендер статьи и индекс парсят markdown двумя разными конвейерами Markdig — расширения должны
/// совпадать, иначе поиск увидит текст иначе, чем страница, и никто не заметит. Проверяем это по
/// поведению: один и тот же текст на конструкциях из SharedPipeline.Builder() (таблица, сноска,
/// вики-ссылка) и CalloutTransformer (коллаут) должен быть виден обеим сторонам.
public class MarkdownPipelinesTests
{
    const string Markdown = """
        | город | страна |
        |---|---|
        | Тверь | Россия |

        текст[^1]

        [^1]: примечание про сноску

        > [!note] Заголовок коллаута
        > в коллауте есть [[proxmox|мой сервер]]
        """;

    [Fact]
    public void Article_and_index_pipelines_see_the_same_constructs()
    {
        var html = new ArticleRenderer().Render(Markdown, "s", NoArticles.Instance);
        var text = PlainText.Extract(Markdown);

        // Таблица: в html она разметкой, в тексте — без палок-разделителей. Без расширения
        // на одной из сторон осталась бы строка с "|".
        Assert.Contains("<table>", html);
        Assert.Contains("Тверь", text);
        Assert.Contains("Россия", text);
        Assert.DoesNotContain("|", text);

        // Сноска.
        Assert.Contains("footnote", html);
        Assert.Contains("примечание про сноску", text);
        Assert.DoesNotContain("[^1]", text);

        // Коллаут: заголовок виден и в html-блоке, и в тексте индекса.
        Assert.Contains("Заголовок коллаута", html);
        Assert.Contains("Заголовок коллаута", text);

        // Вики-ссылка: подпись видна и в <a>, и как обычный текст.
        Assert.Contains("мой сервер", html);
        Assert.Contains("мой сервер", text);
    }
}
