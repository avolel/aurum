using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Macro;

/// <summary>
/// A scheduled publish time for a release (CPI, jobs, PCE). Filled from FRED's release dates; the
/// macro timer wakes on these to fetch a figure minutes after it comes out.
/// </summary>
public class MacroRelease : AuditableEntity
{
    public long Id { get; set; }

    /// <summary>The release's code in <c>FredRelease</c>, e.g. <c>cpi</c>.</summary>
    public string ReleaseCode { get; set; } = null!;

    /// <summary>The publish time, in UTC.</summary>
    public DateTimeOffset ScheduledAt { get; set; }
}
