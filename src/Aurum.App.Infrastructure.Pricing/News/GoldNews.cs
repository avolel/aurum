using System.Text.RegularExpressions;

namespace Aurum.App.Infrastructure.Pricing.News;

/// <summary>
/// Decides whether a feed item is worth keeping: gold itself, or a driver the model reasons about
/// (the Fed, rates, inflation, yields, the dollar). Runs before saving, so a word added here never
/// reaches articles already skipped; after 8 hours those are outside item 6's window anyway (D-29).
/// </summary>
public static partial class GoldNews
{
    public static bool IsRelated(string title, string? extract) =>
        Keywords().IsMatch(title) || (extract is not null && Keywords().IsMatch(extract));

    // \b is a whole-word edge: "Goldman" and "golden" don't match "gold", but "Gold's" and "XAU/USD" do.
    // "Fed up" matches "Fed"; that mistake is accepted. \s+ and [\s-] let a phrase break over a line
    // or be written "safe-haven".
    [GeneratedRegex(
        @"\b(gold|bullion|precious\s+metals?|xau|fed|federal\s+reserve|fomc|interest\s+rates?|inflation|cpi|pce|treasury\s+yields?|dollar|safe[\s-]+haven)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Keywords();
}
