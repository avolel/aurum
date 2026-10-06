namespace Aurum.App.Infrastructure.Pricing.Sources;

internal sealed class SourceCircuit(string sourceCode, TimeProvider clock, PriceFeedCircuitOptions options)
{
    // Request threads read snapshots while the poller writes.
    private readonly Lock _gate = new();

    private int _consecutiveFailures;

    // Null when closed.
    private DateTimeOffset? _openedAt;

    private DateTimeOffset? _lastFailureAt;

    private string? _lastFailureReason;

    // Closes an expired circuit as a side effect.
    public bool IsOpen()
    {
        lock (_gate)
        {
            Settle(clock.GetUtcNow());
            return _openedAt is not null;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openedAt = null;
        }
    }

    public void RecordFailure(string reason)
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            Settle(now);
            _lastFailureAt = now;

            // Already capped by the caller, and never built from a request URI (see SourceAttempt).
            _lastFailureReason = reason;
            _consecutiveFailures++;

            // Only open a closed circuit, or each failure would extend the break.
            if (_openedAt is null && _consecutiveFailures >= options.FailureThreshold)
                _openedAt = now;
        }
    }

    public CircuitSnapshot Snapshot()
    {
        lock (_gate)
        {
            Settle(clock.GetUtcNow());

            return new CircuitSnapshot(
                sourceCode,
                IsOpen: _openedAt is not null,
                ConsecutiveFailures: _consecutiveFailures,

                // Null when closed: null + BreakDuration is null.
                OpenedUntil: _openedAt + options.BreakDuration,
                LastFailureAt: _lastFailureAt,
                LastFailureReason: _lastFailureReason);
        }
    }

    // Caller holds _gate. Closes an expired circuit with the count one short of the threshold,
    // so the next ordinary attempt is the probe (D-14).
    private void Settle(DateTimeOffset now)
    {
        if (_openedAt is { } openedAt && now - openedAt >= options.BreakDuration)
        {
            _openedAt = null;
            _consecutiveFailures = options.FailureThreshold - 1;
        }
    }
}
