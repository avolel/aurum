using Microsoft.Extensions.Logging;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps every entry so a test can assert on what was said.
/// </summary>
/// <remarks>
/// Used where the log line is the deliverable rather than a side effect — the governor's denial
/// message has to name the actual cause, and nothing else observes that.
/// </remarks>
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

    // Every level is enabled: a test that filters by level would silently stop observing the
    // Debug-level denials this exists to check.
    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
