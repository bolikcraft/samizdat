using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace Samizdat.Server.Auth;

public static class AntiforgeryHtml
{
    // Кэшируемые страницы (article.html) подставляют этот плейсхолдер вместо токена текущего гостя,
    // чтобы одна закэшированная страница не разошлась под чужими antiforgery-cookie. См. PageEndpoints.
    public const string Placeholder = "__ANTIFORGERY__";

    /// Готовое скрытое поле формы с actual-токеном текущего запроса.
    public static string Field(IAntiforgery antiforgery, HttpContext context)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return $"""<input type="hidden" name="{HtmlEncoder.Default.Encode(tokens.FormFieldName)}" value="{HtmlEncoder.Default.Encode(tokens.RequestToken!)}">""";
    }

    // UseAntiforgery проверяет запрос сам только когда параметр обработчика привязан к форме
    // ([FromForm]/IFormCollection) — эти маршруты читают форму вручную через HttpContext, поэтому
    // проверяем явно фильтром, а не полагаемся на автовывод RequestDelegateFactory.
    public static TBuilder RequireValidToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.RequireValidToken(_ => Results.BadRequest());

    /// Перегрузка со своим откликом на несовпавший токен — форме входа голый 400 не подходит,
    /// ей нужно увести человека дальше (см. POST /login).
    public static TBuilder RequireValidToken<TBuilder>(this TBuilder builder, Func<HttpContext, IResult> onInvalid)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (invocation, next) =>
        {
            var antiforgery = invocation.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(invocation.HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return onInvalid(invocation.HttpContext);
            }
            return await next(invocation);
        });
}
