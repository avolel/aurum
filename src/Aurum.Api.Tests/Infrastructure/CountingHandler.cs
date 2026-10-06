using System.Diagnostics;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>Counts what actually left the process and when, and answers from a script.</summary>
internal sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<long> _timestamps = [];
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>
    /// <c>Stopwatch.GetTimestamp()</c> as each request entered, read through
    /// <see cref="Stopwatch.GetElapsedTime(long, long)"/> to measure the gaps between retries.
    /// </summary>
    public IReadOnlyList<long> Timestamps
    {
        get { lock (_gate) { return [.. _timestamps]; } }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Before respond(), so the stamp is when the attempt reached the network.
        lock (_gate)
        {
            _timestamps.Add(Stopwatch.GetTimestamp());
        }

        Interlocked.Increment(ref _callCount);
        return Task.FromResult(respond(request));
    }
}
