using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Pricing;

/// <summary>
/// Registry of upstream quote providers. Configuration decides the failover order and whether a
/// source is used (D-20 removed <c>IsEnabled</c> and <c>Priority</c>).
/// </summary>
public class PriceSource : AuditableEntity
{
    /// <summary>Natural key, e.g. <c>goldapi.io</c>. Matches <c>PriceSourceOptions.SourceCode</c>.</summary>
    public string Code { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    /// <summary>Last time a poll of this source succeeded — the input to freshness reporting.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    public DateTimeOffset? LastFailureAt { get; set; }

    public string? LastFailureReason { get; set; }
}
