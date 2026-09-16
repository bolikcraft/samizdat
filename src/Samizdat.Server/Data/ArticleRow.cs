namespace Samizdat.Server.Data;

public sealed class ArticleRow
{
    public required string Slug { get; set; }
    public required string Title { get; set; }
    public string Folder { get; set; } = "";

    /// Имя файла заметки в вольте без .md. По нему находится [[ссылка]], если slug задан
    /// в шапке или взят из title. null — клиент имени не прислал.
    public string? NoteName { get; set; }

    public string? Description { get; set; }
    public DateOnly? Date { get; set; }
    public string? Theme { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ArticleVisibility Visibility { get; set; } = ArticleVisibility.Private;

    /// Когда видимость меняли последний раз. Входит в отпечаток каталога: по нему сбрасывается
    /// кэш страниц, иначе закрытая статья продолжала бы открываться из него.
    public DateTimeOffset? VisibilityChangedAt { get; set; }

    /// Текст статьи без разметки. Поисковые векторы над ним считает сама база.
    public string SearchText { get; set; } = "";

    /// ContentHash на момент сборки индекса. Расходится — сервер достроит индекс при старте.
    public string IndexedHash { get; set; } = "";
}
