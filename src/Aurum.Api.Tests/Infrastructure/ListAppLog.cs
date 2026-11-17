using Aurum.App.Logging;
using Microsoft.Extensions.Logging;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// An <see cref="IAppLogService{T}"/> that keeps every call, for tests where the log row is the
/// deliverable.
/// </summary>
/// <remarks>
/// Locked because a background service under test can log while the test thread reads.
/// </remarks>
public sealed class ListAppLog<T> : IAppLogService<T>
{
    public sealed record Entry(
        LogType LogType, LogLevel Level, string Action, string? Details, int? StatusCode, Exception? Exception);

    private readonly List<Entry> _entries = [];

    public IReadOnlyList<Entry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public Task LogAsync(
        LogType logType,
        string action,
        string? details = null,
        int? statusCode = null,
        long? durationMs = null,
        LogLevel level = LogLevel.Information,
        CancellationToken ct = default)
    {
        Add(new Entry(logType, level, action, details, statusCode, null));
        return Task.CompletedTask;
    }

    public Task LogErrorAsync(
        string action,
        Exception exception,
        int statusCode = 500,
        string? details = null,
        LogType logType = LogType.ActionLog,
        CancellationToken ct = default)
    {
        Add(new Entry(logType, LogLevel.Error, action, details, statusCode, exception));
        return Task.CompletedTask;
    }

    private void Add(Entry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }
}
