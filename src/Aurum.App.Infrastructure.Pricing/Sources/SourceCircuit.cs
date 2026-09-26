namespace Aurum.App.Infrastructure.Pricing.Sources;

internal sealed class SourceCircuit(string sourceCode, TimeProvider clock, PriceFeedCircuitOptions options)
{
    // net10.0's System.Threading.Lock. The poller is single-instance, but item 8 reads snapshots
    // from request threads while the poller writes. A handful of field assignments under one lock;
    // the alternative is a torn read of a state machine.
    private readonly Lock _gate = new();

    //how many polls in a row have failed
    private int _consecutiveFailures;

    //when the shutout started, or null if the source isn't shut out
    private DateTimeOffset? _openedAt;

    // when the last failure happened
    private DateTimeOffset? _lastFailureAt;

    // what it was — capped at 512 chars,
    // and never built from a request URI, because
    // MetalpriceAPI puts its API key in the query string
    private string? _lastFailureReason;

    // closes an expired circuit as a side effect, count left at threshold-1
    public bool IsOpen()
    {
        lock (_gate)
        {
            Settle(clock.GetUtcNow());
            return _openedAt is not null;
        }
    }

    // count to 0, closed              
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

            // Capped, and the caller must never build this from a request URI: MetalpriceAPI
            // puts the API key in the query string, and this value is projected to price_sources.
            _lastFailureReason = reason.Length <= 512 ? reason : reason[..512];
            _consecutiveFailures++;

            // Only open a closed circuit. Without the null check, a failure recorded while the
            // break is still running would restamp _openedAt and extend the shutout indefinitely.
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

                // Nullable arithmetic propagates: null + BreakDuration is null, which is
                // exactly the contract on OpenedUntil ("null when closed").
                OpenedUntil: _openedAt + options.BreakDuration,
                LastFailureAt: _lastFailureAt,
                LastFailureReason: _lastFailureReason);
        }
    }

    // Caller must hold _gate. Applies the passage of time to the state: an open circuit whose
    // break has elapsed becomes closed, with the count parked one short of the threshold so the
    // next ordinary attempt is the probe (D-14).
    private void Settle(DateTimeOffset now)
    {
        if (_openedAt is { } openedAt && now - openedAt >= options.BreakDuration)
        {
            _openedAt = null;
            _consecutiveFailures = options.FailureThreshold - 1;
        }
    }
}
