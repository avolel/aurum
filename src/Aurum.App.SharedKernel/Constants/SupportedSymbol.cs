namespace Aurum.App.SharedKernel.Constants;

public static class SupportedSymbol
{
    public const string Gold = "XAUUSD";
    public const string Silver = "XAGUSD";

    /// <summary>
    /// Every symbol above. Add a new one here too, or the warm-ups skip it.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Gold, Silver];
}
