namespace Aurum.App.SharedKernel.Constants;

public static class SupportedSymbol
{
    public const string Gold = "XAUUSD";
    public const string Silver = "XAGUSD";

    /// <summary>
    /// Every symbol above, for code that has to visit each one (the latest-quote cache's warm-up).
    /// Add a new symbol here as well as above, or it is never warmed.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Gold, Silver];
}
