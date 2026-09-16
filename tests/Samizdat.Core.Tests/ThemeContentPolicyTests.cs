using System.Text.RegularExpressions;
using Samizdat.Core.Themes;

namespace Samizdat.Core.Tests;

/// Сервер отдаёт страницы с политикой script-src 'self'. Встроенный скрипт в шаблоне браузер не выполнит.
public class ThemeContentPolicyTests
{
    public static TheoryData<string> Templates()
    {
        var names = typeof(EmbeddedThemeSource).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("theme/", StringComparison.Ordinal) && name.EndsWith(".html", StringComparison.Ordinal))
            .Select(name => name["theme/".Length..]);
        return new TheoryData<string>(names);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Template_has_no_inline_script(string path)
    {
        var html = new EmbeddedThemeSource().ReadText(path)!;

        Assert.DoesNotMatch(new Regex(@"<script(?![^>]*\bsrc=)[^>]*>", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"\son[a-z]+\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }
}
