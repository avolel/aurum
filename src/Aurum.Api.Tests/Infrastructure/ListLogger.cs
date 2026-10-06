using Microsoft.Extensions.Logging;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps every entry, for tests where the log line is the deliverable.
/// </summary>
public sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => _entries.Add((logLevel, formatter(state, exception)));

    // Every level, so the Debug-level governor denials are captured.
    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
