using Aurum.App.Infrastructure.Pricing;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The <c>PricePolling</c> rules <c>Program.cs</c> runs at boot. Calls the same method the host
/// does, so a test cannot agree with a copy of the rule while the real one drifts.
/// </summary>
public class PricePollingOptionsTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    [Theory]
    [InlineData(null, true)]        // unset: the cache derives twice the interval
    [InlineData("00:15:00.0000001", true)]  // the smallest value strictly above the interval
    [InlineData("00:15:00", false)] // equal: stale one poll after arriving
    [InlineData("00:10:00", false)] // below: stale before the next poll could replace it
    [InlineData("00:00:00", false)]
    public void StaleAfter_must_be_unset_or_longer_than_the_poll_interval(string? staleAfter, bool valid)
    {
        var options = new PricePollingOptions
        {
            PollInterval = PollInterval,
            StaleAfter = staleAfter is null ? null : TimeSpan.Parse(staleAfter, System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.Equal(valid, PricePollingOptions.StaleAfterExceedsPollInterval(options));
    }
}
