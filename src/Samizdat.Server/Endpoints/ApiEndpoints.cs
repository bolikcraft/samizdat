using Samizdat.Server.Auth;

namespace Samizdat.Server.Endpoints;

public static class ApiEndpoints
{
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(ApiToken.Scheme)
                .RequireAuthenticatedUser());

        api.MapGet("/state", () => Results.Ok(new Dictionary<string, string>()));
    }
}
