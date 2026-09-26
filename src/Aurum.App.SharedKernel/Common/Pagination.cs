namespace Aurum.App.SharedKernel.Common;

/// <summary>Page request. Bound from the query string on list endpoints.</summary>
public sealed class PaginationQuery
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Hard ceiling on a caller-supplied page size. <c>price_ticks</c> is a hypertable that grows
    /// by one row per poll forever, so an unbounded page size is a request that reads the table.
    /// </summary>
    public const int MaxPageSize = 500;

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = DefaultPageSize;
}

/// <summary>One page of results plus what the caller needs to ask for the next one.</summary>
public sealed record PageResult<T>(
    IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasNextPage => Page < TotalPages;
}

public static class PaginationHelper
{
    /// <summary>
    /// Clamps a caller-supplied page request into the allowed range.
    /// </summary>
    /// <remarks>
    /// Clamping rather than rejecting: a page size of 0 or a negative page number is almost always
    /// an uninitialised client, and returning 400 for it turns a harmless default into a support
    /// ticket. A page size <em>above</em> the ceiling is clamped too, because the ceiling is there
    /// to protect the database, not to police the caller.
    /// </remarks>
    public static (int Page, int PageSize) GetEffectivePagination(PaginationQuery? pagination)
    {
        var page = Math.Max(1, pagination?.Page ?? 1);
        var requested = pagination?.PageSize ?? PaginationQuery.DefaultPageSize;
        var pageSize = requested <= 0
            ? PaginationQuery.DefaultPageSize
            : Math.Min(requested, PaginationQuery.MaxPageSize);

        return (page, pageSize);
    }

    public static PageResult<T> CreatePageResult<T>(
        IReadOnlyList<T> items, int page, int pageSize, int totalCount) =>
        new(items, page, pageSize, totalCount);
}
