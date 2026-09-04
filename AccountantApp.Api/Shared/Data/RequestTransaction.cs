using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AccountantApp.Api.Shared.Data;

public sealed class RequestTransaction : IRequestTransaction, IAsyncDisposable
{
    private IDbContextTransaction? _transaction;

    public async Task<IAsyncDisposable> BeginAsync(DbContext context, CancellationToken ct)
    {
        // Only the outermost caller owns the transaction. A nested BeginAsync — a handler that
        // begins, then calls another slice's handler that also begins — enlists and gets a scope
        // that does nothing on disposal. If it got an owning scope instead, the inner handler
        // returning would roll the whole request back, the outer CommitAsync would find
        // _transaction already null and no-op, and the caller would get a 200 with nothing
        // committed and no exception anywhere. See Employees' three-slice onboard endpoint.
        if (_transaction is not null)
        {
            await EnlistAsync(context, ct);
            return NoopScope.Instance;
        }

        _transaction = await context.Database.BeginTransactionAsync(ct);
        return new TransactionScope(this);
    }

    public async Task EnlistAsync(DbContext context, CancellationToken ct)
    {
        if (_transaction is null)
            return;

        if (context.Database.CurrentTransaction is null)
            await context.Database.UseTransactionAsync(_transaction.GetDbTransaction(), ct);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        if (_transaction is null)
            return;

        await _transaction.CommitAsync(ct);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is null)
            return;

        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    private sealed class TransactionScope(RequestTransaction owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }

    private sealed class NoopScope : IAsyncDisposable
    {
        public static readonly NoopScope Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
