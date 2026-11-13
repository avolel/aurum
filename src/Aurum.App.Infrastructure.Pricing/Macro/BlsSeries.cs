namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>A BLS series read on release morning, and the FRED series its row is stored under (D-23).</summary>
/// <remarks>All seasonally adjusted, to match the FRED series.</remarks>
public sealed record BlsSeries(string Code, FredSeries Figure)
{
    public static readonly BlsSeries Cpi = new("CUSR0000SA0", FredSeries.Cpi);
    public static readonly BlsSeries CoreCpi = new("CUSR0000SA0L1E", FredSeries.CoreCpi);
    public static readonly BlsSeries NonfarmPayrolls = new("CES0000000001", FredSeries.NonfarmPayrolls);
    public static readonly BlsSeries UnemploymentRate = new("LNS14000000", FredSeries.UnemploymentRate);

    // Declared last: static fields initialise in text order.
    public static readonly IReadOnlyList<BlsSeries> All = [Cpi, CoreCpi, NonfarmPayrolls, UnemploymentRate];
}
