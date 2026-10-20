using System.Threading.RateLimiting;

namespace Samizdat.Server.Storage;

/// Сколько архивов живёт разом. Место занято от начала сборки до конца отдачи: в <DataRoot>/tmp
/// лежит не больше 4 архивов, и собирается не больше 4 из них. Медленные клиенты занимают места
/// надолго, и тогда следующие скачивания получают 503.
public sealed class ZipGate : IDisposable
{
    public const int Parallel = 4;

    readonly ConcurrencyLimiter limiter = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = Parallel, QueueLimit = 0,
    });

    /// IsAcquired == false — все места заняты, архив сейчас не собрать.
    public RateLimitLease TryEnter() => limiter.AttemptAcquire();

    public void Dispose() => limiter.Dispose();
}
