using Samizdat.Core;

namespace Samizdat.Core.Rendering;

/// В вольте цель [[ссылки]] — имя заметки, на сервере адрес статьи — slug. Здесь одно приводится
/// к другому. Одним правилом пользуются и рендер, и сбор обратных ссылок: разойдись они, бэклинк
/// появился бы там, где в тексте ссылки нет.
public static class WikiLinkTarget
{
    /// Кандидаты по убыванию точности: сама цель и её slug.
    public static IReadOnlyList<string> Candidates(string target)
    {
        var name = Clean(target);
        if (name.Length == 0) return [];

        var slug = Slugger.FromTitle(name);
        if (slug == name) return [name];

        // Цель без букв и цифр даёт запасное имя, а оно у чужой статьи своё: ссылка ушла бы не туда.
        return slug == Slugger.Fallback ? [name] : [name, slug];
    }

    /// Без якоря "#раздел" и без пути папки: ссылка ведёт на статью целиком.
    static string Clean(string target)
    {
        var anchor = target.IndexOf('#');
        var name = anchor < 0 ? target : target[..anchor];
        var slash = name.LastIndexOf('/');
        return (slash < 0 ? name : name[(slash + 1)..]).Trim();
    }
}
