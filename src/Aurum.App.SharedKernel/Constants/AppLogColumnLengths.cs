namespace Aurum.App.SharedKernel.Constants;

/// <summary>
/// Column sizes for <c>app_logs</c>, read by the schema and by <c>AppLogService</c>. A value longer
/// than its column fails the whole log batch, so the service cuts every field to these.
/// </summary>
public static class AppLogColumnLengths
{
    public const int MaxActionLength = 128;
    public const int MaxUserAgentLength = 512;
    public const int MaxDetailsLength = 4000;
    public const int MaxExceptionTypeLength = 256;
    public const int MaxStackTraceLength = 8000;
    public const int MaxRequestPathLength = 512;
    public const int MaxUserIdLength = 64;
    public const int MaxTenantIdLength = 64;
    public const int MaxIpAddressLength = 64;
    public const int MaxHttpMethodLength = 16;
    public const int MaxCorrelationIdLength = 64;
    public const int MaxSourceLength = 128;
    public const int MaxLogTypeLength = 32;
}
