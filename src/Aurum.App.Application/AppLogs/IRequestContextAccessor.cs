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
/// This layer must not touch HTTP (<c>docs/best-practices-api.md</c>), so the implementation lives in
/// <c>Aurum.Api</c>. Background jobs get <see cref="RequestContext.None"/>.
/// </remarks>
public interface IRequestContextAccessor
{
    RequestContext Current { get; }
}
