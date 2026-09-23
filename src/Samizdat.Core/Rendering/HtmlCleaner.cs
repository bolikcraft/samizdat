using Ganss.Xss;

namespace Samizdat.Core.Rendering;

/// Чистит готовый html статьи по белому списку. Сырой HTML в заметке остаётся рабочим, но без
/// скриптов, обработчиков on* и адресов со схемами кроме http, https и mailto.
public static class HtmlCleaner
{
    static readonly HashSet<string> RawTextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "textarea", "title", "xmp", "noembed", "noframes",
    };

    /// Рисованные схемы в заметках — это встроенный svg. Пропускаем только фигуры и текст.
    /// Снаружи остаются foreignObject (это html внутри рисунка), use и image (тянут чужой документ)
    /// и анимация: animate умеет подменить адрес в ссылке уже после чистки.
    static readonly string[] SvgTags =
    {
        "svg", "g", "defs", "marker", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "linearGradient", "radialGradient", "stop", "clipPath", "desc",
    };

    static readonly string[] SvgAttributes =
    {
        "viewBox", "preserveAspectRatio", "xmlns", "transform", "d", "points", "x", "y", "x1", "y1", "x2", "y2",
        "cx", "cy", "r", "rx", "ry", "dx", "dy", "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width",
        "stroke-dasharray", "stroke-dashoffset", "stroke-linecap", "stroke-linejoin", "stroke-opacity", "opacity",
        "font-family", "font-size", "font-weight", "font-style", "text-anchor", "dominant-baseline",
        "letter-spacing", "marker-start", "marker-mid", "marker-end", "markerWidth", "markerHeight", "markerUnits",
        "refX", "refY", "orient", "offset", "stop-color", "stop-opacity", "gradientUnits", "gradientTransform",
        "clip-path", "vector-effect", "role", "aria-label", "aria-hidden",
    };

    // Инициализируется после наборов тегов: статические поля создаются в порядке объявления.
    static readonly HtmlSanitizer Sanitizer = Create();

    public static string Clean(string html) => Sanitizer.Sanitize(html);

    static HtmlSanitizer Create()
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "abbr", "audio", "b", "blockquote", "br", "caption", "cite", "code", "col", "colgroup",
            "dd", "del", "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "font",
            "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i", "iframe", "img", "input", "ins", "kbd", "li",
            "mark", "ol", "p", "pre", "q", "s", "samp", "small", "source", "span", "strong", "sub",
            "summary", "sup", "table", "tbody", "td", "tfoot", "th", "thead", "tr", "u", "ul", "video",
        };
        tags.UnionWith(SvgTags);

        var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "align", "allowfullscreen", "alt", "checked", "cite", "class", "color", "colspan", "controls",
            "datetime", "dir", "disabled", "frameborder", "height", "href", "id", "lang", "loop", "muted",
            "open", "poster", "reversed", "rowspan", "scope", "src", "start", "style", "title", "type",
            "width",
        };
        attributes.UnionWith(SvgAttributes);

        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = tags,
            AllowedAttributes = attributes,
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" },
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "href", "src", "cite", "poster",
            },
            AllowedCssProperties = HtmlSanitizerDefaults.AllowedCssProperties,
        })
        {
            // Незнакомый тег убирается, а текст внутри остаётся: иначе <center> из вставки съест абзац.
            KeepChildNodes = true,
        };

        // Чужой сайт во фрейме изолирован от нашего origin только при https-адресе. Относительный
        // адрес открыл бы во фрейме наши же страницы.
        sanitizer.FilterUrl += (_, args) =>
        {
            if (args.Tag.NodeName == "IFRAME"
                && args.SanitizedUrl?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) != true)
                args.SanitizedUrl = null;
        };

        // KeepChildNodes оставил бы код из <script> и <style> видимым текстом на странице.
        sanitizer.RemovingTag += (_, args) =>
        {
            if (RawTextTags.Contains(args.Tag.NodeName)) args.Tag.TextContent = "";
        };

        return sanitizer;
    }
}
