using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <remarks>
/// In memory and never reloaded at startup, unlike <see cref="Quota.IQuotaGovernor"/> (D-14).
/// Every registered source gets an empty circuit up front so the operator view can list them all.
/// </remarks>
internal sealed class SourceCircuitStore
{
    private readonly ConcurrentDictionary<string, SourceCircuit> _circuits;

    public SourceCircuitStore(
        IEnumerable<RegisteredPriceSource> registered,
        TimeProvider clock,
        IOptions<PriceFeedCircuitOptions> options)
    {
        var circuitOptions = options.Value;

        _circuits = new ConcurrentDictionary<string, SourceCircuit>(
            registered.Select(r => KeyValuePair.Create(
                r.SourceCode,
                new SourceCircuit(r.SourceCode, clock, circuitOptions))),

            // Explicit so nobody "fixes" it to IgnoreCase.
            StringComparer.Ordinal);
    }

    // Indexer, not GetOrAdd: an unknown code is a wiring bug and must throw, not report healthy.
    internal SourceCircuit For(string sourceCode) => _circuits[sourceCode];

    public IReadOnlyList<CircuitSnapshot> SnapshotAll() =>
        _circuits.Values.Select(c => c.Snapshot()).ToList();
}
