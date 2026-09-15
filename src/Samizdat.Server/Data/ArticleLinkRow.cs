namespace Samizdat.Server.Data;

/// Вики-ссылка из одной статьи в другую. У ToSlug внешнего ключа нет намеренно: ссылка на ещё
/// не выложенную заметку законна и должна дожить до её выкладки.
public sealed class ArticleLinkRow
{
    public required string FromSlug { get; set; }
    public required string ToSlug { get; set; }
}
