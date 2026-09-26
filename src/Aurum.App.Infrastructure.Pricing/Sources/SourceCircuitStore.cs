using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <remarks>
/// In-memory and never rehydrated, which is the opposite rule from <see cref="Quota.IQuotaGovernor"/>
/// one folder over. The governor persists because a spent request is a fact about the provider's
/// ledger that outlives this process; a circuit is a fact about our own recent observations, and a
/// provider that went down an hour before a deploy is very likely back. Loading an open circuit at
/// boot would blind a new process to a healthy source for a break it never observed (D-14).
/// The circuits here are seeded empty at construction, which is not a load path: an empty circuit
/// is what a fresh process should believe. It exists so the operator endpoint can report every
/// configured source rather than only the ones that have already been polled.
/// </remarks>
internal sealed class SourceCircuitStore
{
    private readonly ConcurrentDictionary<string, SourceCircuit> _circuits;

    public SourceCircuitStore(
        IEnumerable<RegisteredPriceSource> registered,
        TimeProvider clock,
        IOptions<PriceFeedCircuitOptions> options)
    {
        // Read once. Every circuit shares one options instance rather than each re-reading .Value.
        var circuitOptions = options.Value;

        _circuits = new ConcurrentDictionary<string, SourceCircuit>(
            registered.Select(r => KeyValuePair.Create(
                r.SourceCode,
                new SourceCircuit(r.SourceCode, clock, circuitOptions))),

            // Ordinal: source codes are identifiers ("goldapi.io"), not user text. The default
            // comparer is also ordinal, but saying so stops anyone "fixing" it to IgnoreCase.
            StringComparer.Ordinal);
    }

    // Indexer, not GetOrAdd: the key set is fixed at boot by PricingModule's registrations, so an
    // unknown code is a wiring bug. Creating a circuit for it would hide that bug behind a source
    // that reports healthy forever — the same failure as a switch fall-through fabricating config.
    internal SourceCircuit For(string sourceCode) => _circuits[sourceCode];

    public IReadOnlyList<CircuitSnapshot> SnapshotAll() =>
        _circuits.Values.Select(c => c.Snapshot()).ToList();
}
