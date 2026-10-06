using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Pricing;

/// <summary>
/// Registry of upstream quote providers. <see cref="Priority"/> and <see cref="IsEnabled"/> are
/// descriptive only: configuration decides the failover order.
/// </summary>
public class PriceSource : AuditableEntity
{
    /// <summary>Natural key, e.g. <c>goldapi.io</c>. Matches the options key under PriceSources.</summary>
    public string Code { get; set; } = null!;

    public string DisplayName { get; set; } = null!;

    public int Priority { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Last time a poll of this source succeeded — the input to freshness reporting.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    public DateTimeOffset? LastFailureAt { get; set; }

    public string? LastFailureReason { get; set; }
}
