namespace Aurum.App.SharedKernel.Constants;

public static class NewsArticleConstants
{
    // The column lengths. The RSS client cuts or skips to fit them, because Postgres rejects an
    // over-long value and that fails the whole feed's save, not one row.
    public const int MaxSourceLength = 64;
    public const int MaxTitleLength = 512;
    public const int MaxUrlLength = 2048;
    public const int MaxExtractLength = 300;
}
