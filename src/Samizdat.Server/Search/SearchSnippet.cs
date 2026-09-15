using System.Net;

namespace Samizdat.Server.Search;

/// Цитата из ts_headline. Разделители — служебные символы, а не html: ts_headline отдаёт исходный
/// текст как есть, и тег из заметки уехал бы на страницу разметкой.
public static class SearchSnippet
{
    public const char Start = '';
    public const char Stop = '';

    /// Многоточие: разделитель между двумя кусками цитаты и знак её отрезанного края.
    public const string Ellipsis = "…";

    public const string FragmentDelimiter = " " + Ellipsis + " ";

    /// Значения в кавычках: разбор настроек ts_headline обрывает значение на пробеле.
    public static string Options =>
        $"""StartSel="{Start}", StopSel="{Stop}", MaxWords=30, MinWords=15, """
        + $"""MaxFragments=2, FragmentDelimiter="{FragmentDelimiter}" """;

    /// Порядок обязателен: сперва экранируем html, и только потом ставим подсветку.
    public static string ToHtml(string snippet)
        => WebUtility.HtmlEncode(snippet).Replace(Start.ToString(), "<mark>").Replace(Stop.ToString(), "</mark>");
}
