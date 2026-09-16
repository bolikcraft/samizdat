using System.Collections.Concurrent;
using Samizdat.Core.Localization;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using Scriban.Syntax;

namespace Samizdat.Core.Themes;

public sealed class ThemeException(string message) : Exception(message);

public sealed class PageRenderer(IThemeSource theme, Translator? text = null)
{
    readonly ConcurrentDictionary<(string Path, string Version), Template> cache = new();

    public string Render(string templateName, Dictionary<string, object?> model)
    {
        var inner = RenderTemplate(templateName, model);

        var layoutModel = new Dictionary<string, object?>(model) { ["content"] = inner };
        return RenderTemplate("layout.html", layoutModel);
    }

    /// Фрагмент без layout: им рисуются куски, которые нельзя положить в кэш страницы.
    public string RenderPart(string templateName, Dictionary<string, object?> model)
        => RenderTemplate(templateName, model);

    string RenderTemplate(string name, Dictionary<string, object?> model)
    {
        var template = cache.GetOrAdd((name, theme.Version), key => Parse(key.Path));

        // Перевод и код языка нужны каждому шаблону, поэтому кладутся тут, а не в двадцати
        // вызовах Render. Поле модели сильнее: страница может подставить своё значение.
        var script = ToScriptObject(WithText(model));
        var context = new TemplateContext { MemberRenamer = member => member.Name, TemplateLoader = new IncludeLoader(theme) };
        context.PushGlobal(script);
        try
        {
            return template.Render(context);
        }
        // Ошибка внутри include (файла нет, синтаксис сломан, рекурсия ушла за предел Scriban) —
        // это тоже поломка темы, репортим так же, как ошибки самого верхнего шаблона.
        catch (ScriptRuntimeException error)
        {
            throw new ThemeException($"Шаблон {name}: {error.OriginalMessage}");
        }
    }

    Dictionary<string, object?> WithText(Dictionary<string, object?> model)
    {
        if (text is null) return model;

        var full = new Dictionary<string, object?> { ["t"] = text.Model(), ["lang"] = text.Code };
        foreach (var (field, value) in model) full[field] = value;
        return full;
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

    // Отдаёт файлы темы функции include, чтобы шаблон мог звать себя же для вложенных папок
    // (рекурсивное дерево). Расширение можно не указывать: сперва пробуем имя как есть.
    // Глубина ограничена Scriban'ом (TemplateContext.RecursiveLimit) — зациклиться нельзя.
    sealed class IncludeLoader(IThemeSource theme) : ITemplateLoader
    {
        public string? GetPath(TemplateContext context, SourceSpan callerSpan, string templateName)
            => Resolve(templateName) is null ? null : templateName;

        public string? Load(TemplateContext context, SourceSpan callerSpan, string templatePath)
            => Resolve(templatePath);

        public ValueTask<string?> LoadAsync(TemplateContext context, SourceSpan callerSpan, string templatePath)
            => new(Load(context, callerSpan, templatePath));

        string? Resolve(string name) => theme.ReadText(name) ?? theme.ReadText($"{name}.html");
    }
}
