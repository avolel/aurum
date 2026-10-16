namespace Aurum.App.Application.Prices.DTOs;

/// <param name="DisplayName">From <c>price_sources</c>; null when the source has no row.</param>
/// <param name="Priority">From settings, which decide the failover order. Lowest is tried first.</param>
/// <param name="MonthlyRequestLimit">From settings. The quota's own <c>Limit</c> is what this period was opened with.</param>
/// <param name="LastSuccessAt">Written by the poller once per poll; survives restarts.</param>
/// <param name="Circuit">Null when no code is registered for this source, so it is never called.</param>
/// <param name="Quota">Null when the source is switched off: it spends nothing, and a half-configured one can't report a period.</param>
public sealed record PriceSourceStatusDto(
    string SourceCode,
    string? DisplayName,
    int Priority,
    bool Enabled,
    int MonthlyRequestLimit,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureReason,
    CircuitStatusDto? Circuit,
    QuotaStatusDto? Quota);

/// <param name="OpenedUntil">Null when closed.</param>
public sealed record CircuitStatusDto(bool IsOpen, int ConsecutiveFailures, DateTimeOffset? OpenedUntil);

/// <param name="Used">The real count, even after the provider rejected us: the gap is the sign the ledger drifted.</param>
/// <param name="Remaining">0 when <paramref name="ProviderRejected"/>, whatever <c>Limit - Used</c> says (D-7).</param>
public sealed record QuotaStatusDto(int Used, int Limit, int Remaining, DateTimeOffset ResetsAt, bool ProviderRejected);
