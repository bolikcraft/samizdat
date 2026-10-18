namespace Samizdat.Server.Auth;

/// Пока у человека короткий пароль, его cookie несёт SessionCookie.SetupClaim, и сайт для него
/// закрыт: открыты только маршруты с AllowDuringSetup.
// Список разрешённого, а не запрещённого: новый маршрут закрыт сам, о нём не нужно помнить.
public static class SetupGate
{
    sealed class Allowed;

    public static TBuilder AllowDuringSetup<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new Allowed());

    /// Ставить после UseAuthentication: метку читает из уже проверенной cookie.
    public static void UseSetupGate(this WebApplication app) =>
        app.Use((context, next) =>
        {
            if (!SessionCookie.NeedsSetup(context.User)
                || context.GetEndpoint()?.Metadata.GetMetadata<Allowed>() is not null)
                return next(context);

            // Страницу браузер уводим на форму смены. Прочее (API, формы) получает отказ.
            var page = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                       && !context.Request.Path.StartsWithSegments("/api");
            if (page) context.Response.Redirect("/setup");
            else context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
}
