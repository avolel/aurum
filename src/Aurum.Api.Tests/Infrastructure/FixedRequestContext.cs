using Aurum.App.Application.AppLogs;

namespace Aurum.Api.Tests.Infrastructure;

internal sealed class FixedRequestContext(RequestContext current) : IRequestContextAccessor
{
    public RequestContext Current { get; } = current;
}
