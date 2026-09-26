using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Aurum.App.Infrastructure.Data.Repositories;

/// <summary>
/// The write seam for command handlers: save, and run a unit of work in one transaction.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);

    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class UnitOfWork(AurumDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> work, CancellationToken ct = default)
    {
        // Joining an existing transaction rather than nesting. Npgsql does not support nested
        // transactions, so a handler that dispatches a second command through MediatR would throw
        // here — and the correct semantics are that the outer transaction owns the commit anyway.
        if (db.Database.CurrentTransaction is not null)
        {
            return await work(ct);
        }

        // The execution strategy owns the retry loop, and a transaction opened outside it cannot
        // be retried — EF throws telling you so. Everything, including BeginTransaction, goes
        // inside the delegate.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async token =>
        {
            await using IDbContextTransaction transaction =
                await db.Database.BeginTransactionAsync(token);

            var result = await work(token);

            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);

            return result;
        }, ct);
    }
}
