namespace Aurum.App.SharedKernel.Common;

public static class PaginationHelper
{
    public const int DefaultPageSize = 50;

    /// <summary>
    /// Ceiling on page size, so a request cannot read a whole growing table like <c>price_ticks</c>.
    /// </summary>
    public const int MaxPageSize = 500;

    /// <summary>Missing or non-positive → the default; above the ceiling → the ceiling.</summary>
    /// <remarks>
    /// Clamps rather than rejects: bad values are usually an uninitialised client, and the ceiling
    /// protects the database, not the caller.
    /// </remarks>
    public static int ClampPageSize(int? requested) =>
        requested is null or <= 0 ? DefaultPageSize : Math.Min(requested.Value, MaxPageSize);
}
