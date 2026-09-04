using AccountantApp.Api.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.TestDoubles;

// The InMemory provider does not support transactions, so the flow tests — which exist to
// exercise handler rules, not the transaction guarantee — need an IRequestTransaction that
// does nothing. This double lives in the test project on purpose: the production
// RequestTransaction must not carry an "if this store cannot do transactions, silently skip
// them" branch, because that branch would also fire for any real misconfiguration and turn the
// cross-slice atomicity guarantee off without a word.
//
// The tests that actually verify transactional behaviour (CustomersSchemaTests,
// TicketTypesSchemaTests) run against real PostgreSQL and use the real RequestTransaction.
public sealed class NoOpRequestTransaction : IRequestTransaction
{
    public Task<IAsyncDisposable> BeginAsync(DbContext context, CancellationToken ct) =>
        Task.FromResult<IAsyncDisposable>(NoopScope.Instance);

    public Task EnlistAsync(DbContext context, CancellationToken ct) => Task.CompletedTask;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    private sealed class NoopScope : IAsyncDisposable
    {
        public static readonly NoopScope Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
