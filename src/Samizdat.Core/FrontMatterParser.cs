using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Samizdat.Core;

public static class FrontMatterParser
{
    static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ParsedDocument Parse(string text)
    {
        if (!StartsWithFence(text, out var afterOpening))
            return new ParsedDocument(new FrontMatter(), text);

        var closing = FindClosingFence(text, afterOpening);
        if (closing.LineStart < 0)
            return new ParsedDocument(new FrontMatter(), text);

        var yaml = text[afterOpening..closing.LineStart];
        var body = text[closing.BodyStart..];

        try
        {
            var matter = Yaml.Deserialize<FrontMatter>(yaml) ?? new FrontMatter();
            return new ParsedDocument(matter, body);
        }
        catch (YamlException error)
        {
            throw new FrontMatterException(
                $"Сломан фронтматтер, строка {error.Start.Line + 1}: {error.Message}", error);
        }
    }

    static bool StartsWithFence(string text, out int afterOpening)
    {
        afterOpening = 0;
        if (!text.StartsWith("---", StringComparison.Ordinal)) return false;

        var lineEnd = text.IndexOf('\n');
        if (lineEnd < 0) return false;
        if (text[3..lineEnd].Trim().Length != 0) return false;

        afterOpening = lineEnd + 1;
        return true;
    }

    static (int LineStart, int BodyStart) FindClosingFence(string text, int from)
    {
        var at = from;
        while (at < text.Length)
        {
            var lineEnd = text.IndexOf('\n', at);
            var end = lineEnd < 0 ? text.Length : lineEnd;
            if (text[at..end].TrimEnd() == "---")
                return (at, lineEnd < 0 ? text.Length : lineEnd + 1);
            if (lineEnd < 0) break;
            at = lineEnd + 1;
        }
        return (-1, -1);
    }
}
