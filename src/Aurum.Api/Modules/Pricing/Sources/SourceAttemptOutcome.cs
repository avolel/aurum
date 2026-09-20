namespace Aurum.Api.Modules.Pricing.Sources;

public enum SourceAttemptOutcome
{
    Success,
    /// <summary>Unusable, transport failure or timeout. A circuit fault.</summary>
    Faulted,
    /// <summary>Denied or rejected. Not a fault — the source is healthy and broke.</summary>
    QuotaExhausted,
    /// <summary>Not called; the circuit was open. Produced no new evidence.</summary>
    SkippedCircuitOpen
}
