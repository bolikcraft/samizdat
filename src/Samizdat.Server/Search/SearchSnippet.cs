using System.Net;

namespace Samizdat.Server.Search;

/// Цитата из ts_headline. Разделители — служебные символы, а не html: ts_headline отдаёт исходный
/// текст как есть, и тег из заметки уехал бы на страницу разметкой.
public static class SearchSnippet
{
    public const char Start = '';
    public const char Stop = '';

    /// Разделитель между двумя кусками цитаты — тоже управляющий символ, не текст: иначе он
    /// совпадает с авторским «…» в самой статье, и strpos/split_part путают
    /// свой разделитель с чужим текстом (split_part на таком краю отдаёт
    /// пустую строку, а strpos с пустой иглой находит её всегда, в позиции 1).
    public const char FragmentBreak = '';

    /// Многоточие: видимый знак между кусками цитаты и на её отрезанном краю.
    public const string Ellipsis = "…";

    /// Значения в кавычках: разбор настроек ts_headline обрывает значение на пробеле.
    public static string Options =>
        $"""StartSel="{Start}", StopSel="{Stop}", MaxWords=30, MinWords=15, """
        + $"""MaxFragments=2, FragmentDelimiter="{FragmentBreak}" """;

    /// Порядок обязателен: сперва экранируем html, и только потом ставим подсветку.
    public static string ToHtml(string snippet)
        => WebUtility.HtmlEncode(snippet).Replace(Start.ToString(), "<mark>").Replace(Stop.ToString(), "</mark>")
            .Replace(FragmentBreak.ToString(), $" {Ellipsis} ");
}
