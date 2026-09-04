using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AccountantApp.Api.Slices.Tickets.Infrastructure;

/// <summary>
/// The seam over the allocator, and the ONLY reason it exists is testability.
///
/// <c>CreateTicketHandler</c> decides six values that can never be changed afterwards (§4.1), which makes
/// it the handler in this slice whose behaviour matters most -- and it was the one handler with no
/// in-memory test at all, because it depended on the concrete allocator, whose single statement is raw
/// SQL the in-memory provider cannot execute. So every rule in that handler was verified only by reading,
/// and its happy path not at all.
///
/// This interface changes nothing about how a reference is allocated in production: exactly one
/// implementation exists, it is the class below, and the rules in its doc comment are unchanged and still
/// binding. Do NOT add a second production implementation, and in particular do not add an in-memory one
/// as a "fallback" -- the atomicity is the entire point, and a fallback would silently hand out duplicate
/// references the moment it were selected.
/// </summary>
public interface ITicketReferenceAllocator
{
    Task<string> AllocateAsync(int year, CancellationToken ct);
}

/// <summary>
/// Allocates the human-readable ticket reference, TKT-{year}-{000000}. Plan section 1.7.
///
/// This is the ONE piece of raw SQL in the slice, and it is raw because the statement it needs cannot
/// be expressed through the change tracker. The sequence restarts each year, which rules out a plain
/// PostgreSQL SEQUENCE, so a counter table plus one atomic upsert does the work:
///
///     INSERT ... VALUES (@year, 1) ON CONFLICT (year) DO UPDATE
///         SET last_sequence = ticket_reference_counters.last_sequence + 1
///     RETURNING last_sequence;
///
/// It is safe under any concurrency because ON CONFLICT DO UPDATE takes a row lock and RETURNING reads
/// the value it just wrote. Fifty concurrent creations therefore serialise on that one row and get
/// fifty distinct numbers.
///
/// Three things not to do, each of which looks fine and is a duplicate reference waiting to happen:
///
/// 1. Do NOT read-then-increment. SELECT last_sequence followed by UPDATE is a lost-update race that
///    produces two tickets with one reference, and uq_tickets_reference then rejects the second with a
///    500 at the worst possible moment.
/// 2. Do NOT use COUNT(*) FROM tickets. It is a race AND it reuses numbers after a cancellation, and
///    the reference must never be reused.
/// 3. Do NOT call NOW() inside the statement while formatting the year in C#. On New Year's Eve the two
///    disagree and the reference contradicts its own sequence. The year is passed in, resolved once
///    from the application clock by the caller.
///
/// GAPS ARE CORRECT AND REQUIRED. The allocation happens inside the creation transaction, so a
/// rolled-back creation consumes a number and leaves a hole. "Never reused" is the stated requirement
/// (01-DomainModel.md section 3) and gaps are its price -- section 12 constraint 5. Do not add
/// compaction, a gap-filling scan, or a "reclaim on rollback" step.
/// </summary>
public sealed class TicketReferenceAllocator : ITicketReferenceAllocator
{
    private readonly TicketsDbContext _db;

    public TicketReferenceAllocator(TicketsDbContext db) => _db = db;

    /// <summary>
    /// Use exactly this statement. It is atomic; anything decomposed into a read and a write is not.
    /// </summary>
    private const string AllocateSql = """
        INSERT INTO ticket_reference_counters (year, last_sequence)
        VALUES (@year, 1)
        ON CONFLICT (year) DO UPDATE
            SET last_sequence = ticket_reference_counters.last_sequence + 1
        RETURNING last_sequence;
        """;

    /// <summary>
    /// Allocates the next sequence number for <paramref name="year"/> and formats the reference.
    ///
    /// Call this INSIDE the creation transaction, on the same DbContext the ticket is being written
    /// through, so the number is consumed by a rollback rather than handed to the next request.
    /// </summary>
    /// <param name="year">
    /// The year, resolved once from the application clock by the caller. Never read from the database.
    /// </param>
    public async Task<string> AllocateAsync(int year, CancellationToken ct)
    {
        var sequence = await AllocateSequenceAsync(year, ct);
        return Format(year, sequence);
    }

    /// <summary>
    /// Zero-padded to six digits. The domain model's example is TKT-2026-000417, which is six; five or
    /// seven would sort differently as text and would not match a client that formats it back.
    /// </summary>
    public static string Format(int year, int sequence) => $"TKT-{year}-{sequence:D6}";

    private async Task<int> AllocateSequenceAsync(int year, CancellationToken ct)
    {
        // Executed as a DbCommand rather than through SqlQueryRaw, because EF composes a SqlQuery into
        // an outer SELECT ... FROM (sql) when an operator such as SingleAsync is applied, and an
        // INSERT ... RETURNING cannot appear in a FROM clause. This also keeps the statement byte-for-
        // byte the one documented above.
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await _db.Database.OpenConnectionAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = AllocateSql;

        // Enlists in the request's transaction. Without this the counter row commits on its own, so a
        // rolled-back creation would NOT consume the number -- which sounds like an improvement and is
        // the read-then-increment race in disguise, because the row lock is released early.
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();

        var parameter = command.CreateParameter();
        parameter.ParameterName = "year";
        parameter.DbType = DbType.Int32;
        parameter.Value = year;
        command.Parameters.Add(parameter);

        var scalar = await command.ExecuteScalarAsync(ct);
        if (scalar is null or DBNull)
            throw new InvalidOperationException(
                "Allocating a ticket reference returned no sequence number. The "
                + "ticket_reference_counters table is missing or the migration has not been applied.");

        return Convert.ToInt32(scalar);
    }
}
