namespace Aurum.App.Infrastructure.Pricing.Macro;

public sealed class MacroPollingOptions
{
    public const string SectionName = "Macro:Polling";

    public TimeSpan RefreshInterval { get; set; }

    // Read only for a series with no rows, so changing it later reloads nothing until the
    // macro tables are emptied. 2006 is when the broad dollar index (DTWEXBGS) begins.
    public DateOnly? HistoryStart { get; set; }
}
