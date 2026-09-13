using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Samizdat.Server.Data;

namespace Samizdat.Server.Auth;

public static class ApiToken
{
    public const string Scheme = "ApiToken";

    public static string Create() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string HashOf(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    SamizdatDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var hash = ApiToken.HashOf(header["Bearer ".Length..].Trim());
        var row = await db.ApiTokens.FirstOrDefaultAsync(token => token.TokenHash == hash);
        if (row is null) return AuthenticateResult.Fail("Токен не найден");

        row.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var user = await db.Users.FirstAsync(item => item.Id == row.UserId);
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, user.Login), new Claim(ClaimTypes.Role, user.Role.ToString())],
            ApiToken.Scheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), ApiToken.Scheme));
    }
}
