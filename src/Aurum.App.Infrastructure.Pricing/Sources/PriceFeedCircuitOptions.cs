namespace Aurum.App.Infrastructure.Pricing.Sources;

public class PriceFeedCircuitOptions
{
    // Failures in a row before the circuit opens.
    public int FailureThreshold { get; set; } = 3;
    // How long it stays open.
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromMinutes(15);
    public const string SectionName = "PriceFeedCircuit";
}
