using System.Threading.RateLimiting;

namespace Samizdat.Server.Storage;

/// Сколько архивов собирается разом. Сборка читает все вложения статьи и сжимает их: залп
/// скачиваний не должен занять весь процессор и все потоки. Готовый архив ворота не держат.
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
