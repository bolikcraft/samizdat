using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Samizdat.Server.Auth;

/// Пределы для анонимных форм с паролем: вход, открытая регистрация, приглашение.
public static class AuthLimits
{
    public const string Policy = "password-forms";

    public static void AddAuthLimits(this WebApplicationBuilder builder)
    {
        var attempts = builder.Configuration.GetValue("Samizdat:Auth:AttemptsPerMinute", 10);
        var parallel = Math.Max(1, builder.Configuration.GetValue("Samizdat:Auth:ParallelHashes", 2));
        var queue = Math.Max(0, builder.Configuration.GetValue("Samizdat:Auth:HashQueue", 32));
        // Через фабрику: экземпляр, созданный контейнером, контейнер и освобождает.
        builder.Services.AddSingleton(_ => new PasswordGate(parallel, queue));

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
            // Адрес берём у соединения, а не из X-Forwarded-For: заголовок подделает любой,
            // кто ходит на порт мимо прокси.
            options.AddPolicy(Policy, context => attempts <= 0
                ? RateLimitPartition.GetNoLimiter("")
                : RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = attempts, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
                    }));
        });
    }
}
