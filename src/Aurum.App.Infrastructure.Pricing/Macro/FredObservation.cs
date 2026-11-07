namespace Aurum.App.Infrastructure.Pricing.Macro;

public sealed record FredObservation(DateOnly ObservedOn, decimal? Value, DateOnly? ReleasedOn);
