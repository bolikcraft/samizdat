using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Samizdat.Server.Auth;

namespace Samizdat.Server.Search;

/// Предел для /search: каждый запрос строит цитаты по всем статьям в базе.
public static class SearchLimits
{
    public const string Policy = "search";

    const int PerMinute = 60;

    // Код 429 и Retry-After ставит общий обработчик отказов из AuthLimits.
    public static void AddSearchLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options => options.AddPolicy(Policy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                // Приставка разводит счета: логин «10.0.0.1» не делит окно с этим адресом.
                context.User.Identity?.Name is { } login
                    ? $"login:{login}"
                    : $"ip:{ClientAddress.LimitKey(context.Connection.RemoteIpAddress)}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                })));
}
