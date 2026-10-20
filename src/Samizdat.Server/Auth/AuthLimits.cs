using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Samizdat.Server.Auth;

/// Пределы для форм с паролем: вход, открытая регистрация, приглашение, смена своего пароля.
public static class AuthLimits
{
    public const string Policy = "password-forms";

    public static void AddAuthLimits(this WebApplicationBuilder builder)
    {
        var attempts = builder.Configuration.GetValue("Samizdat:Auth:AttemptsPerMinute", 10);
        var parallel = Math.Max(1, builder.Configuration.GetValue("Samizdat:Auth:ParallelHashes", 2));
        var queue = Math.Max(0, builder.Configuration.GetValue("Samizdat:Auth:HashQueue", 32));
        var failuresPerLogin = builder.Configuration.GetValue("Samizdat:Auth:FailuresPerLogin", 20);
        // Через фабрику: экземпляр, созданный контейнером, контейнер и освобождает.
        builder.Services.AddSingleton(_ => new PasswordGate(parallel, queue));
        builder.Services.AddSingleton(new LoginFailures(failuresPerLogin, TimeProvider.System));

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var after))
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(after.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            // Адрес соединения после UseForwardedHeaders: за прокси из Samizdat:Proxy это адрес клиента,
            // а X-Forwarded-For от прочих отправителей сюда не попадает.
            options.AddPolicy(Policy, context => attempts <= 0
                ? RateLimitPartition.GetNoLimiter("")
                : RateLimitPartition.GetFixedWindowLimiter(
                    ClientAddress.LimitKey(context.Connection.RemoteIpAddress),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = attempts, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    }));
        });
    }
}
