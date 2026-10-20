using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Samizdat.Server.Tests;

sealed class CapturingLoggerProvider(LogLevel minimum = LogLevel.Warning) : ILoggerProvider
{
    public ConcurrentBag<string> Messages { get; } = [];

    LogLevel Minimum { get; } = minimum;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    sealed class CapturingLogger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= owner.Minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                 Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) owner.Messages.Add(formatter(state, exception));
        }
    }
}
