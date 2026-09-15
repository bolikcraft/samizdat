using YamlDotNet.Serialization;

namespace Samizdat.Core;

/// Исходник статьи для того, кто не владелец: тело как есть, а шапка собрана заново из полей,
/// которые и так видны на странице. Чужие поля не уходят вместе с файлом, и `publish: true`
/// не выложит заметку в чужом вольте.
public static class PublicSource
{
    // Перевод строки задан явно: по умолчанию эмиттер берёт системный, и на Windows файл уезжал бы
    // с \r\n, а на Linux с \n — сравнивать такое в тестах нечем.
    static readonly ISerializer Yaml = new SerializerBuilder().WithNewLine("\n").Build();

    public static string Of(string text)
    {
        var parsed = FrontMatterParser.Parse(text);
        var matter = parsed.FrontMatter;

        // Словарь, а не объект: порядок полей задаётся здесь и виден глазами.
        var fields = new Dictionary<string, string>();
        if (matter.Title is { Length: > 0 } title) fields["title"] = title;
        if (matter.Description is { Length: > 0 } description) fields["description"] = description;
        if (matter.Date is { } date) fields["date"] = date.ToString("yyyy-MM-dd");

        return fields.Count == 0 ? parsed.Body : $"---\n{Yaml.Serialize(fields)}---\n{parsed.Body}";
    }
}
