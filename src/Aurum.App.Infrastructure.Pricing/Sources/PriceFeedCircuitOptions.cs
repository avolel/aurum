namespace Aurum.App.Infrastructure.Pricing.Sources;

public class PriceFeedCircuitOptions
{
    // failures in a row before shutting a source out
    public int FailureThreshold { get; set; } = 3;
    // how long the shutout lasts
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(15);
    public const string SectionName = "PriceFeedCircuit";
}
