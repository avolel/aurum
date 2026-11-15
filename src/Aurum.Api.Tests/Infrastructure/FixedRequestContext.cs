using Aurum.App.Logging;

namespace Aurum.Api.Tests.Infrastructure;

internal sealed class FixedRequestContext(RequestContext current) : IRequestContextAccessor
{
    public RequestContext Current { get; } = current;
}
