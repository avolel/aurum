using Aurum.App.Infrastructure.Data.Entities.Pricing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Aurum.Api.Tests.Infrastructure;

/// <summary>
/// Fails any save that adds a tick observed at <paramref name="failAt"/>. Raw SQL never passes
/// through SaveChanges, so the price_events insert is untouched.
/// </summary>
public sealed class FailingTickSaveInterceptor(DateTimeOffset failAt) : SaveChangesInterceptor
{
    // RunContinuationsAsynchronously: the test's await must not resume on the poller's thread
    // before the throw below has happened.
    private readonly TaskCompletionSource _tripped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the failing save is reached, which is the poll's last step.</summary>
    public Task Tripped => _tripped.Task;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        // Match on ObservedAt rather than "any tick", so the first poll's save still succeeds
        var failing = eventData.Context!.ChangeTracker.Entries<PriceTick>()
            .Any(e => e.State == EntityState.Added && e.Entity.ObservedAt == failAt);

        if (!failing)
        {
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        _tripped.TrySetResult();
        throw new DbUpdateException("Tick save failed on purpose.");
    }
}
