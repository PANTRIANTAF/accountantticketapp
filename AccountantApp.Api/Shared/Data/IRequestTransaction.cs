using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Api.Shared.Data;

public interface IRequestTransaction
{
    Task<IAsyncDisposable> BeginAsync(DbContext context, CancellationToken ct);
    Task EnlistAsync(DbContext context, CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}