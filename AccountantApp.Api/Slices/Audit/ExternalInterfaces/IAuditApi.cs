namespace AccountantApp.Api.Slices.Audit.ExternalInterfaces;

public sealed record AuditEntry(
    string Action,
    string TargetKind,
    string TargetId,
    Guid? CustomerId = null,
    string Outcome = AuditOutcome.Success,
    object? Before = null,
    object? After = null);

public static class AuditOutcome
{
    public const string Success = "Success";
    public const string Denied = "Denied";
    public const string Failure = "Failure";

    // Kept in sync with the CHECK on audit_entries.outcome.
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Success, Denied, Failure };
}

public interface IAuditApi
{
    Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    // No default implementation. A default body that throws NotSupportedException turns "this
    // implementation forgot a method" from a compile error into a runtime failure inside an audit
    // write — the worst place to discover it, because the operation being audited fails with it.
    // Both members are part of the contract; an implementation that cannot do one is incomplete.
    Task LogUnauthenticatedAsync(
        string actorIdentifier,
        AuditEntry entry,
        CancellationToken cancellationToken = default);
}