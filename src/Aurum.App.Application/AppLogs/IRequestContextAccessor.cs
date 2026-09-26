namespace Aurum.App.Application.AppLogs;

/// <summary>
/// The ambient facts about the inbound request that every log row is enriched with.
/// </summary>
/// <param name="UserId">Null outside a request — background jobs have no user.</param>
/// <param name="CorrelationId">Ties every row produced by one inbound request together.</param>
public sealed record RequestContext(
    string? UserId = null,
    string? TenantId = null,
    string? IpAddress = null,
    string? UserAgent = null,
    string? RequestPath = null,
    string? HttpMethod = null,
    string? CorrelationId = null)
{
    /// <summary>What a background job sees: no request, so no enrichment.</summary>
    public static readonly RequestContext None = new();
}

/// <summary>
/// Supplies <see cref="RequestContext"/> without the Application layer referencing
/// <c>IHttpContextAccessor</c>.
/// </summary>
/// <remarks>
/// This interface exists for one reason: the layer table in <c>docs/best-practices-api.md</c> gives
/// this project "Never Does: Direct HTTP/controller concerns", and <c>HttpContext</c> in a handler
/// is precisely that. The implementation lives in <c>Aurum.Api</c>, where HTTP is the subject.
/// The second benefit is that a background job — <c>PricePollingService</c>, the log drain — gets
/// <see cref="RequestContext.None"/> rather than a null-reference from an absent HttpContext.
/// </remarks>
public interface IRequestContextAccessor
{
    RequestContext Current { get; }
}
