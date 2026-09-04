using AccountantApp.Api.Slices.Identity.Application.Dtos;
using AccountantApp.Api.Slices.Identity.Core;

namespace AccountantApp.Api.Slices.Identity.Application;

/// <summary>
/// Entity to DTO, in one place. No DTO produced here has a password hash, token hash, failure count or
/// lockout timestamp, and that is the reason the mapper exists rather than each handler projecting for
/// itself: a field that is never mapped anywhere cannot be leaked by the one handler that forgot.
/// </summary>
public static class IdentityMapper
{
    /// <summary>AccountantAdmin only. Carries the login email.</summary>
    public static AccountantDetailDto ToDetailDto(UserAccount account) => new(
        account.Id,
        account.DisplayName,
        account.LoginEmail,
        account.Role,
        account.Status,
        account.CreatedAt,
        account.LastLoginAt);

    /// <summary>
    /// What an AccountantUser sees: name and identifier, nothing else. A separate TYPE rather than the
    /// detail DTO with fields nulled out -- a type with no LoginEmail property cannot leak one.
    /// </summary>
    public static AccountantSummaryDto ToSummaryDto(UserAccount account) => new(
        account.Id,
        account.DisplayName);

    /// <summary>
    /// For audit Before/After. Status, role and email only: the fields whose change is worth recording.
    /// Never the hash -- not truncated, not fingerprinted, not at all.
    /// </summary>
    public static object ToAuditSnapshot(UserAccount account) => new
    {
        account.LoginEmail,
        account.DisplayName,
        account.Role,
        account.Status
    };
}
