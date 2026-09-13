using System.Collections.Concurrent;
using Scriban;
using Scriban.Runtime;

namespace Samizdat.Core.Themes;

public sealed class ThemeException(string message) : Exception(message);

public sealed class PageRenderer(IThemeSource theme)
{
    readonly ConcurrentDictionary<(string Path, string Version), Template> cache = new();

    public string Render(string templateName, Dictionary<string, object?> model)
    {
        var inner = RenderTemplate(templateName, model);

        var layoutModel = new Dictionary<string, object?>(model) { ["content"] = inner };
        return RenderTemplate("layout.html", layoutModel);
    }

    string RenderTemplate(string name, Dictionary<string, object?> model)
    {
        var template = cache.GetOrAdd((name, theme.Version), key => Parse(key.Path));

        var script = ToScriptObject(model);
        var context = new TemplateContext { MemberRenamer = member => member.Name };
        context.PushGlobal(script);
        return template.Render(context);
    }

    // Scriban читает вложенные объекты только через ScriptObject/IScriptObject,
    // обычный Dictionary<string, object?> для него не объект с полями — заворачиваем рекурсивно,
    // чтобы модель могла оставаться плоскими словарями.
    static ScriptObject ToScriptObject(Dictionary<string, object?> model)
    {
        var script = new ScriptObject();
        foreach (var (field, value) in model)
            script.Add(field, ToScriptValue(value));
        return script;
    }

    static object? ToScriptValue(object? value) =>
        value is Dictionary<string, object?> nested ? ToScriptObject(nested) : value;

    Template Parse(string name)
    {
        var text = theme.ReadText(name) ?? throw new ThemeException($"В теме нет файла {name}");
        var template = Template.Parse(text, name);
        if (template.HasErrors)
            throw new ThemeException($"Шаблон {name}: {string.Join("; ", template.Messages)}");
        return template;
    }
}
