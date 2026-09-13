using System.Text;

namespace Samizdat.Core;

public static class Slugger
{
    const string Fallback = "bez-nazvaniya";

    static readonly Dictionary<char, string> Cyrillic = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e",
        ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k",
        ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    public static string FromTitle(string title)
    {
        var result = new StringBuilder(title.Length);
        foreach (var symbol in title.ToLowerInvariant())
        {
            if (Cyrillic.TryGetValue(symbol, out var latin))
                result.Append(latin);
            else if (char.IsAsciiLetterOrDigit(symbol))
                result.Append(symbol);
            else
                result.Append('-');
        }

        var slug = string.Join('-', result.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? Fallback : slug;
    }
}
