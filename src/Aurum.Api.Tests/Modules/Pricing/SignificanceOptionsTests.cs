using System.ComponentModel.DataAnnotations;
using Aurum.App.Infrastructure.Pricing.Deltas;

namespace Aurum.Api.Tests.Modules.Pricing;

/// <summary>
/// The <c>Significance</c> rules <c>Program.cs</c> runs at boot. Calls the same methods the host does.
/// </summary>
public class SignificanceOptionsTests
{
    [Fact]
    public void A_valid_configuration_passes_every_rule()
    {
        var options = Valid();

        Assert.True(SignificanceOptions.WindowKeysAreKnown(options));
        Assert.True(SignificanceOptions.ValuesAreInRange(options));
        Assert.True(Validator.TryValidateObject(options, new ValidationContext(options), null, true));
    }

    [Theory]
    [InlineData("5min")] // a typo would switch the 5m window off silently
    [InlineData("2d")]
    public void An_unknown_window_key_is_refused(string key)
    {
        var options = Valid();
        options.Windows[key] = new SignificanceWindowOptions { MinPercent = 0.25m, Cooldown = TimeSpan.FromMinutes(5) };

        Assert.False(SignificanceOptions.WindowKeysAreKnown(options));
    }

    [Fact]
    public void Window_keys_match_ignoring_case()
    {
        var options = Valid();
        options.Windows["1H"] = options.Windows["1h"];
        options.Windows.Remove("1h");

        Assert.True(SignificanceOptions.WindowKeysAreKnown(options));
    }

    [Fact]
    public void A_missing_window_is_allowed()
    {
        var options = Valid();
        options.Windows.Remove("1m");

        Assert.True(SignificanceOptions.WindowKeysAreKnown(options));
        Assert.True(SignificanceOptions.ValuesAreInRange(options));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.25")]
    public void A_non_positive_threshold_is_refused(string minPercent)
    {
        var options = Valid();
        options.Windows["5m"].MinPercent = decimal.Parse(minPercent, System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(SignificanceOptions.ValuesAreInRange(options));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    public void A_non_positive_cooldown_is_refused(string cooldown)
    {
        var options = Valid();
        options.Windows["5m"].Cooldown = TimeSpan.Parse(cooldown, System.Globalization.CultureInfo.InvariantCulture);

        Assert.False(SignificanceOptions.ValuesAreInRange(options));
    }

    [Theory]
    [InlineData("1", true)]      // same bar as a same-source move
    [InlineData("0.99", false)]  // would make a cross-source move easier to fire
    [InlineData("0", false)]
    public void Multiplier_must_be_at_least_one(string multiplier, bool valid)
    {
        var options = Valid();
        options.CrossSourceMagnitudeMultiplier = decimal.Parse(multiplier, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(valid, SignificanceOptions.ValuesAreInRange(options));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_profile_is_refused(string profile)
    {
        var options = Valid();
        options.ThresholdProfile = profile;

        Assert.False(Validator.TryValidateObject(options, new ValidationContext(options), null, true));
    }

    private static SignificanceOptions Valid()
    {
        var options = new SignificanceOptions { ThresholdProfile = "test-v1", CrossSourceMagnitudeMultiplier = 2m };
        foreach (var window in DeltaWindow.All)
        {
            options.Windows[window.Code] = new SignificanceWindowOptions { MinPercent = 0.25m, Cooldown = window.Length };
        }

        return options;
    }
}
