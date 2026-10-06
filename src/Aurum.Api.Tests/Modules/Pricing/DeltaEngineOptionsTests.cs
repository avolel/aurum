using Aurum.App.Infrastructure.Pricing.Deltas;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The <c>DeltaEngine</c> coverage rule <c>Program.cs</c> runs at boot. Calls the same method the
/// host does.
/// </summary>
public class DeltaEngineOptionsTests
{
    /// <summary>
    /// The buffer must reach back one day plus the 1d window's slack (12 hours at the default 0.5).
    /// Expectations are typed in by hand: 36 hours is 129,600 seconds, so at a 10-second interval
    /// that is 12,960 intervals plus the newest sample.
    /// </summary>
    [Theory]
    [InlineData("00:15:00", 8_640, true)]    // shipped cadence: needs 145
    [InlineData("00:15:00", 145, true)]
    [InlineData("00:15:00", 144, false)]
    [InlineData("00:00:10", 8_640, false)]   // the spec's default at its own 10s example: exactly one day
    [InlineData("00:00:10", 12_961, true)]
    [InlineData("00:00:10", 12_960, false)]
    public void Buffer_must_cover_one_day_plus_slack(string pollInterval, int capacity, bool valid)
    {
        var options = new DeltaEngineOptions { MaxSamplesPerSymbol = capacity };
        var interval = TimeSpan.Parse(pollInterval, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(valid, DeltaEngineOptions.BufferCoversLongestWindow(options, interval));
    }

    [Fact]
    public void Lookback_is_one_day_plus_half_a_day_by_default()
    {
        Assert.Equal(TimeSpan.FromHours(36), new DeltaEngineOptions().Lookback);
    }

    [Fact]
    public void A_non_positive_interval_is_left_to_the_PricePolling_validator()
    {
        Assert.True(DeltaEngineOptions.BufferCoversLongestWindow(new DeltaEngineOptions(), TimeSpan.Zero));
    }
}
