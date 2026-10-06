using Microsoft.Extensions.DependencyInjection;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// Counts scopes opened through it. A warm-up opens one scope per load, so this is how a test
/// tells "one shared load" from "a load per caller" without a database-level hook.
/// </summary>
public sealed class CountingScopeFactory(IServiceScopeFactory inner) : IServiceScopeFactory
{
    private int _created;

    public int Created => Volatile.Read(ref _created);

    public IServiceScope CreateScope()
    {
        Interlocked.Increment(ref _created);
        return inner.CreateScope();
    }
}
