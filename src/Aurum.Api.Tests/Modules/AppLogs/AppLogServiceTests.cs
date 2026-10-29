using Aurum.Api.Tests.Infrastructure;
using Aurum.App.Application.AppLogs;
using Aurum.App.Infrastructure.Data.Entities.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aurum.Api.Tests.Modules.AppLogs;

/// <summary>
/// Every queued row fits <c>app_logs</c>. No container.
/// </summary>
/// <remarks>
/// Sizes are typed by hand, not read from <c>AppLogColumnLengths</c>: the schema reads those too, so
/// a test using them would agree with itself.
/// </remarks>
public class AppLogServiceTests
{
    private static readonly string TooLong = new('x', 10_000);

    private static (AppLogService<AppLogServiceTests> Service, AppLogQueue Queue) Build(RequestContext context)
    {
        var queue = new AppLogQueue(NullLogger<AppLogQueue>.Instance);
        var service = new AppLogService<AppLogServiceTests>(
            queue, new FixedRequestContext(context), new FakeTimeProvider());
        return (service, queue);
    }

    private static AppLog Single(AppLogQueue queue)
    {
        Assert.True(queue.TryRead(out var entry));
        Assert.False(queue.TryRead(out _));
        return entry;
    }

    [Fact]
    public async Task LogAsync_LongUserAgent_CutTo512()
    {
        var (service, queue) = Build(new RequestContext(UserAgent: TooLong));

        await service.LogAsync(LogType.ActionLog, "GetSources");

        Assert.Equal(512, Single(queue).UserAgent!.Length);
    }

    [Fact]
    public async Task LogAsync_EveryFieldTooLong_EachCutToItsColumn()
    {
        var (service, queue) = Build(new RequestContext(
            TooLong, TooLong, TooLong, TooLong, TooLong, TooLong, TooLong));

        await service.LogAsync(LogType.ActionLog, TooLong);

        var entry = Single(queue);
        Assert.Equal(128, entry.Action.Length);
        Assert.Equal(64, entry.UserId!.Length);
        Assert.Equal(64, entry.TenantId!.Length);
        Assert.Equal(64, entry.IpAddress!.Length);
        Assert.Equal(512, entry.UserAgent!.Length);
        Assert.Equal(512, entry.RequestPath!.Length);
        Assert.Equal(16, entry.HttpMethod!.Length);
        Assert.Equal(64, entry.CorrelationId!.Length);
    }

    [Fact]
    public async Task LogErrorAsync_LongAction_CutTo128()
    {
        var (service, queue) = Build(RequestContext.None);

        await service.LogErrorAsync(TooLong, new InvalidOperationException("boom"));

        Assert.Equal(128, Single(queue).Action.Length);
    }

    [Theory]
    [InlineData(512, 512)]
    [InlineData(513, 512)]
    public async Task LogAsync_UserAgentAtTheBoundary_KeptOrCut(int sent, int stored)
    {
        var (service, queue) = Build(new RequestContext(UserAgent: new string('x', sent)));

        await service.LogAsync(LogType.ActionLog, "GetSources");

        Assert.Equal(stored, Single(queue).UserAgent!.Length);
    }

    [Fact]
    public async Task LogAsync_ShortAndNullValues_KeptAsIs()
    {
        var (service, queue) = Build(new RequestContext(UserAgent: "curl/8.5.0", HttpMethod: "GET"));

        await service.LogAsync(LogType.ActionLog, "GetSources");

        var entry = Single(queue);
        Assert.Equal("curl/8.5.0", entry.UserAgent);
        Assert.Equal("GET", entry.HttpMethod);
        Assert.Null(entry.UserId); // null must not become ""
    }
}
