namespace Aurum.App.Infrastructure.Pricing.Sources;

// The shape. One lock acquisition, one consistent set of values.
public sealed record CircuitSnapshot(
    string SourceCode,
    bool IsOpen,
    int ConsecutiveFailures,
    // when the shutout ends; null when closed
    DateTimeOffset? OpenedUntil,
    DateTimeOffset? LastFailureAt,
    string? LastFailureReason
);
