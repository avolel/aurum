using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// Tries the configured sources in priority order and returns the first quote anyone produces.
/// </summary>
/// <remarks>
/// <para>
/// A separate interface from <see cref="IPriceSource"/> rather than a composite implementation of
/// it: a composite would be resolved into the same <c>IEnumerable&lt;IPriceSource&gt;</c> it
/// consumes and find itself in its own chain, and <see cref="PriceFeedResult"/> carries statements
/// about a chain that mean nothing on a single provider.
/// </para>
/// <para>
/// There is deliberately <b>no retry here.</b> Retries live in the Polly pipeline below
/// <see cref="IPriceSource"/>, registered in <c>Program.cs</c>, so by the time an exception reaches
/// this loop the source has already exhausted its attempts and charged a lease for each one.
/// Adding a retry at this level would multiply the request budget by a factor the boot-time cadence
/// guard knows nothing about.
/// </para>
/// </remarks>
internal sealed class FailoverPriceFeed(
    IEnumerable<IPriceSource> sources,
    IOptions<PriceSourcesOptions> options,
    SourceCircuitStore circuits,
    ILogger<FailoverPriceFeed> logger) : IPriceFeed
{
    /// <summary>
    /// <c>price_sources.LastFailureReason</c> is <c>nvarchar(512)</c>. Truncating where the string
    /// is built rather than where it is saved: an unbounded provider message throws 22001 out of
    /// SaveChangesAsync, which loses the tick that was fetched in the same unit of work.
    /// </summary>
    private const int MaxFailureReasonLength = 512;

    public async Task<PriceFeedResult> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        var chain = BuildChain();
        var attempts = new List<SourceAttempt>(chain.Count);

        // No enabled, configured, registered source. PriceSourcesOptionsValidator refuses this at
        // boot, so it is a backstop rather than an expected state — but it must not be reported as
        // "every source is out of quota", which is what an empty AllQuotaExhausted would say if it
        // did not guard on Count.
        if (chain.Count == 0)
        {
            throw new AllSourcesFailedException(symbol, attempts);
        }

        var primarySourceCode = chain[0].Code;

        foreach (var source in chain)
        {
            var circuit = circuits.For(source.Code);

            if (circuit.IsOpen())
            {
                // No call, so no new evidence about this source. The attempt is recorded so the
                // operator projection can tell "skipped" from "never in the chain", but it is
                // excluded from AttemptedSources and it never overwrites LastFailureReason.
                attempts.Add(new SourceAttempt(source.Code, SourceAttemptOutcome.SkippedCircuitOpen));
                continue;
            }

            try
            {
                var quote = await source.GetLatestQuoteAsync(symbol, ct);
                circuit.RecordSuccess();
                attempts.Add(new SourceAttempt(source.Code, SourceAttemptOutcome.Success));
                return new PriceFeedResult(quote, primarySourceCode, attempts);
            }
            catch (QuotaExhaustedException ex)
            {
                // Healthy and broke. Not a circuit fault: opening on this would keep the source
                // out of the chain after its period rolls and its budget is fresh, and the
                // expire-to-closed machine has no probe to leak, so quota simply does not touch it.
                attempts.Add(new SourceAttempt(
                    source.Code,
                    SourceAttemptOutcome.QuotaExhausted,
                    Truncate($"Quota exhausted until {ex.ResetsAt:O}."),
                    ex,
                    ex.ResetsAt));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown is not three failing providers. Rethrowing unwrapped keeps the poller's
                // own cancellation clause the thing that handles it.
                throw;
            }
            catch (Exception ex) when (ex is PriceSourceException
                                          or HttpRequestException
                                          or TimeoutRejectedException
                                          or OperationCanceledException)
            {
                // PriceSourceException lands here and nowhere else: the Polly predicate above
                // IPriceSource cannot observe it, because every source parses the body after the
                // pipeline has already judged the HTTP outcome successful. This loop is the only
                // place a provider returning 200s full of junk is counted as a fault, and that is
                // the argument D-14 rests on.
                //
                // A bare OperationCanceledException with ct not cancelled is a per-attempt timeout
                // that escaped as cancellation rather than TimeoutRejectedException — a fault
                // against this source, not a shutdown.
                attempts.Add(RecordFault(circuit, source.Code, ex));
            }
            catch (ArgumentException)
            {
                // An invalid symbol. No source can serve it, so failing over is pointless work
                // that spends a lease per provider to reach the same answer.
                throw;
            }
            catch (Exception ex)
            {
                // A defect in one source must not take down a chain whose whole purpose is
                // availability. Logged with its type name because an unexpected type here is a
                // bug report, not an operational event.
                logger.LogError(ex,
                    "{Source} threw an unexpected {ExceptionType}; treating it as a fault.",
                    source.Code, ex.GetType().Name);
                attempts.Add(RecordFault(circuit, source.Code, ex));
            }
        }

        throw new AllSourcesFailedException(symbol, attempts);
    }

    private static SourceAttempt RecordFault(SourceCircuit circuit, string sourceCode, Exception ex)
    {
        var reason = Truncate(ex.Message);
        circuit.RecordFailure(reason);
        return new SourceAttempt(sourceCode, SourceAttemptOutcome.Faulted, reason, ex);
    }

    /// <summary>
    /// The chain: enabled and configured sources in failover order, each matched to its registered
    /// implementation.
    /// </summary>
    /// <remarks>
    /// Driven from configuration rather than from the resolved services, so a source that is
    /// registered in code but absent from or disabled in the file is simply not in the chain. That
    /// is the bug this class fixes: <c>PricePollingService</c> used to select straight off
    /// <c>IEnumerable&lt;IPriceSource&gt;</c>, which has no Enabled to filter on, so setting
    /// <c>Enabled: false</c> shortened the startup log and changed nothing about selection.
    /// </remarks>
    private List<IPriceSource> BuildChain()
    {
        var registered = sources.ToList();

        return
        [
            .. options.Value.EnabledInFailoverOrder()
                .Select(configured => registered.FirstOrDefault(source =>
                    string.Equals(source.Code, configured.SourceCode, StringComparison.OrdinalIgnoreCase)))
                .OfType<IPriceSource>()
        ];
    }

    private static string Truncate(string reason) =>
        reason.Length <= MaxFailureReasonLength ? reason : reason[..MaxFailureReasonLength];
}
