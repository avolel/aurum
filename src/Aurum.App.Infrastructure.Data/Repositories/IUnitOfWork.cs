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
        // Join, don't nest: Npgsql has no nested transactions, and the outer one owns the commit.
        if (db.Database.CurrentTransaction is not null)
        {
            return await work(ct);
        }

        // EF requires the transaction to open inside the execution strategy's retry delegate.
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
