namespace Aurum.App.SharedKernel.Constants;

public static class SupportedSymbol
{
    public const string Gold = "XAUUSD";
    public const string Silver = "XAGUSD";

    /// <summary>
    /// Every symbol above. Add a new one here too, or the warm-ups skip it.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Gold, Silver];

    /// <summary>The stored spelling of <paramref name="symbol"/>, matched ignoring case; null if unsupported.</summary>
    /// <remarks>
    /// Compare stored values exactly afterwards: SignalR group names are case-sensitive, and a
    /// case-insensitive SQL comparison skips the <c>(Symbol, ObservedAt)</c> index.
    /// </remarks>
    public static string? Find(string? symbol) =>
        All.FirstOrDefault(s => string.Equals(s, symbol, StringComparison.OrdinalIgnoreCase));
}
