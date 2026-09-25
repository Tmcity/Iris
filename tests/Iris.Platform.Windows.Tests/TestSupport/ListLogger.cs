using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Iris.Platform.Windows.Tests.TestSupport;

public sealed record LogEntry(
    LogLevel LogLevel,
    EventId EventId,
    string Message,
    Exception? Exception);

public sealed class ListLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        _entries.Enqueue(new LogEntry(logLevel, eventId, message, exception));
    }

    public bool Contains(string substring, LogLevel? level = null)
    {
        return _entries.Any(e => (level == null || e.LogLevel == level) && e.Message.Contains(substring, StringComparison.Ordinal));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
