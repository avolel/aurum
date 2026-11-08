namespace Aurum.App.Infrastructure.Pricing.Macro;

/// <summary>A FRED series the app loads.</summary>
/// <param name="IsRelease">
/// True for figures published on a release date, which are fetched as first published values (D-22).
/// </param>
public sealed record FredSeries(string Code, string Title, string Units, string Frequency, bool IsRelease)
{
    public const string Provider = "fred"; // matches MacroSeries.Provider

    public static readonly FredSeries Cpi =
        new("CPIAUCSL", "Consumer Price Index for All Urban Consumers", "Index 1982-1984=100", "Monthly", IsRelease: true);

    public static readonly FredSeries Pce =
        new("PCEPI", "Personal Consumption Expenditures Price Index", "Index 2017=100", "Monthly", IsRelease: true);

    // BLS figures read from FRED, because BLS's own API serves revised values only (D-22).
    public static readonly FredSeries CoreCpi =
        new("CPILFESL", "Consumer Price Index for All Urban Consumers: All Items Less Food and Energy", "Index 1982-1984=100", "Monthly", IsRelease: true);

    public static readonly FredSeries NonfarmPayrolls =
        new("PAYEMS", "All Employees, Total Nonfarm", "Thousands of Persons", "Monthly", IsRelease: true);

    public static readonly FredSeries UnemploymentRate =
        new("UNRATE", "Unemployment Rate", "Percent", "Monthly", IsRelease: true);

    // Daily with no release date; rate decisions come from the release calendar instead.
    public static readonly FredSeries FedFundsRate =
        new("DFF", "Federal Funds Effective Rate", "Percent", "Daily", IsRelease: false);

    public static readonly FredSeries Treasury10Y =
        new("DGS10", "10-Year Treasury Constant Maturity Rate", "Percent", "Daily", IsRelease: false);

    public static readonly FredSeries Treasury2Y =
        new("DGS2", "2-Year Treasury Constant Maturity Rate", "Percent", "Daily", IsRelease: false);

    // The Fed's broad dollar index, not ICE's DXY, which FRED does not carry.
    public static readonly FredSeries DollarIndex =
        new("DTWEXBGS", "Nominal Broad U.S. Dollar Index", "Index Jan 2006=100", "Daily", IsRelease: false);

    public static readonly FredSeries Vix =
        new("VIXCLS", "CBOE Volatility Index: VIX", "Index", "Daily", IsRelease: false);

    // Declared last: static fields initialise in text order.
    public static readonly IReadOnlyList<FredSeries> All =
        [Cpi, Pce, CoreCpi, NonfarmPayrolls, UnemploymentRate, FedFundsRate, Treasury10Y, Treasury2Y, DollarIndex, Vix];
}
