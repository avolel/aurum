namespace Aurum.App.Infrastructure.Pricing.Macro;

public sealed record BlsObservation(BlsSeries Series, DateOnly ObservedOn, decimal? Value);
