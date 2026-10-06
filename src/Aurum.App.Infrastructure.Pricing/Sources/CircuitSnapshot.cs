namespace Aurum.App.Infrastructure.Pricing.Sources;

// Taken under one lock, so the values are consistent with each other.
public sealed record CircuitSnapshot(
    string SourceCode,
    bool IsOpen,
    int ConsecutiveFailures,
    // Null when closed.
    DateTimeOffset? OpenedUntil,
    DateTimeOffset? LastFailureAt,
    string? LastFailureReason
);
