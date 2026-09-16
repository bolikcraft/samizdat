using System.Globalization;
using System.Text;

namespace Samizdat.Core;

public static class Slugger
{
    /// Адрес для заголовка без букв и цифр.
    public const string Fallback = "bez-nazvaniya";

    static readonly Dictionary<char, string> Letters = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e",
        ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k",
        ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
        ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ґ'] = "g", ['ў'] = "u",
        // Эти латинские буквы не раскладываются на основу и знак.
        ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe", ['ø'] = "o", ['ł'] = "l", ['đ'] = "d",
        ['ð'] = "d", ['þ'] = "th", ['ı'] = "i", ['ħ'] = "h",
    };

    public static string FromTitle(string title) => TryFromTitle(title) ?? Fallback;

    /// null, если переводить было нечего. Отличает пустой разбор от заголовка, который сам
    /// дал слово-заглушку.
    public static string? TryFromTitle(string title)
    {
        // Имя файла с macOS приходит в NFD. Без сборки «й» распалась бы на «и» и знак.
        var text = title.Normalize(NormalizationForm.FormC).ToLowerInvariant();

        var slug = Join(Latin(text));
        // Письмо без латинской записи оставляем своими буквами: заглушка была бы общей
        // у всех таких заметок, и второй push упал бы на повторе slug.
        if (slug.Length == 0) slug = Join(Native(text));

        slug = Cut(slug);
        return slug.Length == 0 ? null : slug;
    }

    // Перебор по Rune: половинка суррогатной пары ломает Normalize.
    static string Latin(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var symbol in text.EnumerateRunes())
        {
            // Таблицу смотрим до разложения: «й» в NFD — это «и» и знак.
            if (symbol.IsBmp && Letters.TryGetValue((char)symbol.Value, out var latin))
                result.Append(latin);
            else if (symbol.IsAscii && Rune.IsLetterOrDigit(symbol))
                result.Append(symbol.ToString());
            else
                AppendWithoutMarks(result, symbol);
        }
        return result.ToString();
    }

    static void AppendWithoutMarks(StringBuilder result, Rune symbol)
    {
        // «İ» остаётся заглавной после ToLowerInvariant и становится «I» с точкой после разложения.
        var parts = symbol.ToString().Normalize(NormalizationForm.FormD).ToLowerInvariant();
        foreach (var part in parts.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark) continue;

            if (part.IsBmp && Letters.TryGetValue((char)part.Value, out var latin)) result.Append(latin);
            else result.Append(part.IsAscii && Rune.IsLetterOrDigit(part) ? part.ToString() : "-");
        }
    }

    static string Native(string text)
    {
        var symbols = text.EnumerateRunes().ToArray();
        var result = new StringBuilder(text.Length);
        // Знак без буквы перед ним и соединитель вне слова дали бы slug из невидимых символов.
        var afterLetter = false;
        for (var i = 0; i < symbols.Length; i++)
        {
            var symbol = symbols[i];
            if (Rune.IsLetterOrDigit(symbol))
                afterLetter = true;
            else if (afterLetter && IsMark(symbol))
                afterLetter = true;
            // ZWNJ и ZWJ в персидском и в индийских письмах — часть написания слова.
            else if (afterLetter && IsJoiner(symbol) && i + 1 < symbols.Length && Rune.IsLetterOrDigit(symbols[i + 1]))
                afterLetter = false;
            else
            {
                result.Append('-');
                afterLetter = false;
                continue;
            }
            result.Append(symbol.ToString());
        }
        return result.ToString();
    }

    // Гласные знаки хинди и тайского — отдельные символы. Без них слово распалось бы на куски.
    static bool IsMark(Rune symbol)
        => Rune.GetUnicodeCategory(symbol) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    static bool IsJoiner(Rune symbol) => symbol.Value is 0x200C or 0x200D;

    static string Join(string text) => string.Join('-', text.Split('-', StringSplitOptions.RemoveEmptyEntries));

    static string Cut(string slug)
    {
        if (Encoding.UTF8.GetByteCount(slug) <= SafeName.MaxSlugBytes) return slug;

        var end = 0;
        var bytes = 0;
        while (end < slug.Length)
        {
            var step = char.IsHighSurrogate(slug[end]) && end + 1 < slug.Length ? 2 : 1;
            var size = Encoding.UTF8.GetByteCount(slug.AsSpan(end, step));
            if (bytes + size > SafeName.MaxSlugBytes) break;
            bytes += size;
            end += step;
        }
        return slug[..end].TrimEnd('-', '\u200C', '\u200D');
    }
}
