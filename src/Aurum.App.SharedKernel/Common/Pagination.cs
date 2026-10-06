namespace Aurum.App.SharedKernel.Common;

/// <summary>Page request. Bound from the query string on list endpoints.</summary>
public sealed class PaginationQuery
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Ceiling on page size, so a request cannot read a whole growing table like <c>price_ticks</c>.
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
    /// Clamps rather than rejects: bad values are usually an uninitialised client, and the ceiling
    /// protects the database, not the caller.
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
}
