using AccountantApp.Api.Shared.Auth;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Audit.Application;
using AccountantApp.Api.Slices.Audit.Core;
using AccountantApp.Api.Slices.Audit.Infrastructure;

namespace AccountantApp.Api.Slices.Audit.ExternalInterfaces;

public sealed class AuditApi : IAuditApi
{
    private readonly AuditDbContext _db;
    private readonly IRequestTransaction _transaction;
    private readonly IHttpContextAccessor _http;
    private readonly IServiceProvider _services;
    private readonly ILogger<AuditApi> _logger;

    public AuditApi(
        AuditDbContext db,
        IRequestTransaction transaction,
        IHttpContextAccessor http,
        IServiceProvider services,
        ILogger<AuditApi> logger)
    {
        _db = db;
        _transaction = transaction;
        _http = http;
        _services = services;
        _logger = logger;
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        var user = _services.GetRequiredService<CurrentUser>();
        await AppendAsync(user.Id, user.Role.ToString(), entry, cancellationToken);
    }

    public Task LogUnauthenticatedAsync(
        string actorIdentifier,
        AuditEntry entry,
        CancellationToken cancellationToken = default) =>
        AppendAsync(actorIdentifier, "Unknown", entry, cancellationToken);

    private async Task AppendAsync(
        string actorId,
        string actorRole,
        AuditEntry entry,
        CancellationToken ct)
    {
        if (!AuditActions.All.Contains(entry.Action))
            throw new InvalidOperationException($"'{entry.Action}' is not in the audit action catalogue.");
        if (!AuditTargets.All.Contains(entry.TargetKind))
            throw new InvalidOperationException($"'{entry.TargetKind}' is not in the audit target catalogue.");
        // Outcome is checked for the same reason Action and TargetKind are: it is a string with
        // three legal values, the audit reader filters on it, and a typo ("Success " or "Denied.")
        // would silently produce a row that no filter ever matches. The column has a matching
        // CHECK, but a validation failure here names the constant, where a 23514 does not.
        if (!AuditOutcome.All.Contains(entry.Outcome))
            throw new InvalidOperationException($"'{entry.Outcome}' is not an audit outcome.");

        await _transaction.EnlistAsync(_db, ct);
        var request = _http.HttpContext?.Request;
        _db.AuditEntries.Add(new AuditRecord
        {
            Action = entry.Action,
            ActorUserId = Truncate(actorId, 100),
            ActorRole = Truncate(actorRole, 30),
            CustomerId = entry.CustomerId,
            TargetKind = entry.TargetKind,
            TargetId = Truncate(entry.TargetId, 100),
            Outcome = entry.Outcome,
            BeforeValue = Redaction.ToJson(entry.Before, _logger),
            AfterValue = Redaction.ToJson(entry.After, _logger),
            OccurredAt = DateTimeOffset.UtcNow,
            SourceIp = Truncate(_http.HttpContext?.Connection.RemoteIpAddress?.ToString() ?? string.Empty, 45),
            UserAgent = Truncate(request?.Headers.UserAgent.ToString() ?? string.Empty, 512)
        });
        await _db.SaveChangesAsync(ct);
    }

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}