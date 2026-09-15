using Samizdat.Core.Rendering;

namespace Samizdat.Core;

public sealed class FrontMatter
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Slug { get; set; }
    public string? Theme { get; set; }
    public DateOnly? Date { get; set; }
    public bool Publish { get; set; }

    /// Страница берёт заголовок и описание прямо из файла, в обход ArticleIndexer — чистит их сама,
    /// сразу после разбора, одним вызовом на оба поля.
    public void RemoveControlCharacters()
    {
        Title = Title is null ? null : PlainText.WithoutControls(Title);
        Description = Description is null ? null : PlainText.WithoutControls(Description);
    }
}

public sealed record ParsedDocument(FrontMatter FrontMatter, string Body);

public sealed class FrontMatterException(string message, Exception? inner = null)
    : Exception(message, inner);
