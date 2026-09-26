using Aurum.App.SharedKernel.Entities;

namespace Aurum.App.Infrastructure.Data.Entities.Logging;

/// <summary>
/// One durable application-log row. Written by the drain, never by a request thread.
/// </summary>
/// <remarks>
/// Deliberately not a Timescale hypertable, unlike <c>price_ticks</c>. Its access pattern is
/// "find the rows for this correlation id" rather than "scan a time range", and a hypertable would
/// force the partitioning column into the primary key for no read that benefits from it. If this
/// table outgrows retention it gets a partial index and a delete job, not a conversion.
/// </remarks>
public class AppLog : AuditableEntity
{
    public long Id { get; set; }

    /// <summary>The <c>LogType</c> name, stored as text so a new member is not a migration.</summary>
    public string LogType { get; set; } = null!;

    /// <summary>What was attempted, e.g. <c>GetPriceSources</c>. Not a message — a stable key.</summary>
    public string Action { get; set; } = null!;

    public string? Details { get; set; }

    public int? StatusCode { get; set; }

    public string? ExceptionType { get; set; }

    public string? StackTrace { get; set; }

    /// <summary>How long the handled request took. Null for log rows not tied to a request.</summary>
    public long? DurationMs { get; set; }

    public string? UserId { get; set; }

    public string? TenantId { get; set; }

    public string? IpAddress { get; set; }

    public string? UserAgent { get; set; }

    public string? RequestPath { get; set; }

    public string? HttpMethod { get; set; }

    /// <summary>Ties every row produced by one inbound request together.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The category the log came from, i.e. the <c>T</c> of <c>IAppLogService&lt;T&gt;</c>.
    /// </summary>
    public string? Source { get; set; }
}
