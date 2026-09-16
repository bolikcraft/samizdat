using Samizdat.Core.Localization;
using Samizdat.Core.Themes;
using Samizdat.Server.Data;

namespace Samizdat.Server.Endpoints;

/// Страницы для постороннего: он пришёл по ссылке, меню и боковика у него нет. Гостевая ссылка,
/// приглашение и открытая запись отвечают на мёртвый адрес одинаково, поэтому страницы общие.
public static class GuestPages
{
    internal static IResult NotFound(PageRenderer pages, SiteSettings settings, Translator text)
        => Results.Content(pages.Render("404.html", new()
        {
            ["page_title"] = text["error.not_found.title"],
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
        }), "text/html; charset=utf-8", statusCode: 404);

    /// Адрес был и больше не работает: истёк, отозван или уже использован.
    internal static IResult Gone(PageRenderer pages, SiteSettings settings, Translator text)
        => Results.Content(pages.Render("share-expired.html", new()
        {
            ["page_title"] = text["share.expired.title"],
            ["site"] = PageEndpoints.SiteModel(settings),
            ["noindex"] = true,
        }), "text/html; charset=utf-8", statusCode: 410);
}
