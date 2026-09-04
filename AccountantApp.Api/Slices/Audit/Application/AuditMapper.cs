using System.Linq.Expressions;
using AccountantApp.Api.Slices.Audit.Application.Dtos;
using AccountantApp.Api.Slices.Audit.Core;

namespace AccountantApp.Api.Slices.Audit.Application;

public static class AuditMapper
{
    /// <summary>
    /// List projection, as an expression so EF translates it into the SELECT list.
    /// </summary>
    /// <remarks>
    /// This has to stay an <see cref="Expression"/> rather than becoming a static method with a
    /// body. A method called inside <c>Select</c> either fails to translate or is evaluated on the
    /// client after every column has been fetched — including <c>before_value</c> and
    /// <c>after_value</c>, which is exactly what keeping them off the list DTO was for. Written
    /// this way, PostgreSQL is never asked for those two columns on a search.
    /// </remarks>
    public static readonly Expression<Func<AuditRecord, AuditEntryDto>> ToDto = record =>
        new AuditEntryDto
        {
            Id = record.Id,
            ActorUserId = record.ActorUserId,
            ActorRole = record.ActorRole,
            CustomerId = record.CustomerId,
            Action = record.Action,
            TargetKind = record.TargetKind,
            TargetId = record.TargetId,
            Outcome = record.Outcome,
            OccurredAt = record.OccurredAt,
            SourceIp = record.SourceIp,
            UserAgent = record.UserAgent
        };

    /// <summary>
    /// Detail projection. An ordinary method: the detail handler materialises one entry, so there
    /// is nothing to translate and no per-row cost.
    /// </summary>
    public static AuditEntryDetailDto ToDetailDto(AuditRecord record) =>
        new()
        {
            Id = record.Id,
            ActorUserId = record.ActorUserId,
            ActorRole = record.ActorRole,
            CustomerId = record.CustomerId,
            Action = record.Action,
            TargetKind = record.TargetKind,
            TargetId = record.TargetId,
            Outcome = record.Outcome,
            OccurredAt = record.OccurredAt,
            SourceIp = record.SourceIp,
            UserAgent = record.UserAgent,
            BeforeValue = record.BeforeValue,
            AfterValue = record.AfterValue
        };
}
