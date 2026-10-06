using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Aurum.App.Infrastructure.Pricing.Sources;

/// <summary>
/// Tries the configured sources in priority order and returns the first quote anyone produces.
/// </summary>
/// <remarks>
/// <para>Not an <see cref="IPriceSource"/> itself, or it would resolve into its own chain.</para>
/// <para><b>No retry here.</b> Retries are in the pipeline below each source; one here would
/// multiply spend past what the startup budget check counts (D-15).</para>
/// </remarks>
internal sealed class FailoverPriceFeed(
    IEnumerable<IPriceSource> sources,
    IOptions<PriceSourcesOptions> options,
    SourceCircuitStore circuits,
    ILogger<FailoverPriceFeed> logger) : IPriceFeed
{
    /// <summary>
    /// <c>price_sources.LastFailureReason</c> is 512 characters. Cut here, where the string is built:
    /// an over-long message fails SaveChangesAsync and loses the tick saved with it.
    /// </summary>
    private const int MaxFailureReasonLength = 512;

    public async Task<PriceFeedResult> GetLatestQuoteAsync(string symbol, CancellationToken ct)
    {
        var chain = BuildChain();
        var attempts = new List<SourceAttempt>(chain.Count);

        // Reachable: an enabled entry whose code was never registered passes the validator.
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
                // Recorded so "skipped" differs from "not in the chain", but it is not a call.
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
                // Healthy and out of budget: not a circuit fault (D-14).
                attempts.Add(new SourceAttempt(
                    source.Code,
                    SourceAttemptOutcome.QuotaExhausted,
                    Truncate($"Quota exhausted until {ex.ResetsAt:O}."),
                    ex,
                    ex.ResetsAt));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not a failing provider. The poller handles it.
                throw;
            }
            catch (Exception ex) when (ex is PriceSourceException
                                          or HttpRequestException
                                          or TimeoutRejectedException
                                          or OperationCanceledException)
            {
                // The only place a 200 full of junk counts as a fault (D-14). A cancellation
                // without ct cancelled is a timeout, so a fault too.
                attempts.Add(RecordFault(circuit, source.Code, ex));
            }
            catch (ArgumentException)
            {
                // An invalid symbol. No source can serve it, so failing over would only spend leases.
                throw;
            }
            catch (Exception ex)
            {
                // A bug in one source must not take down the chain; the type name marks it as a bug.
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
    /// Driven from configuration, so a disabled or unconfigured source is not in the chain.
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
