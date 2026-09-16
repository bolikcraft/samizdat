using System.Threading.RateLimiting;

namespace Samizdat.Server.Auth;

/// Сколько Argon2id считается разом на анонимных формах. Каждый расчёт берёт 64 МБ, поэтому
/// залп запросов ждёт здесь, а сверх очереди получает отказ.
public sealed class PasswordGate(int parallel, int queue) : IDisposable
{
    readonly ConcurrencyLimiter limiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = parallel,
        QueueLimit = queue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });

    /// Место под один расчёт. IsAcquired == false — очередь полна, хэш считать нельзя.
    public ValueTask<RateLimitLease> Enter(CancellationToken cancel) => limiter.AcquireAsync(1, cancel);

    public static IResult Busy() => Results.StatusCode(StatusCodes.Status429TooManyRequests);

    public void Dispose() => limiter.Dispose();
}
