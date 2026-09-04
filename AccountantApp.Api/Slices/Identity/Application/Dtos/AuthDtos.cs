using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Slices.Identity.Application.Dtos;

// Requests are classes with settable properties, bound from a body. Responses are records.
//
// No DTO in this slice has a PasswordHash, TokenHash, status reason, or FailedLoginCount field:
// nothing outside this slice has any use for them.

public sealed class LoginRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Answers "who am I and where must I go next". Deliberately carries no email and no token -- it is
/// not an account-detail response.
/// </summary>
public sealed record SessionDto(
    string UserId,
    string DisplayName,
    UserRole Role,
    Guid? CustomerId,
    bool MustChangePassword);

/// <summary>
/// Two fields, and there is deliberately no target user. Matrix section 11: "Reset another person's
/// password directly -- Nobody." A userId here would be the vulnerability, so the field must not
/// exist: you cannot forget to validate a parameter you never accepted.
/// </summary>
public sealed class ChangePasswordRequestDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class RequestPasswordResetRequestDto
{
    public string Email { get; set; } = string.Empty;
}

public sealed class CompletePasswordResetRequestDto
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class AcceptInvitationRequestDto
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;

    /// <summary>
    /// Optional override: the inviter typed a placeholder and the person knows their own name.
    /// Absent means keep the existing value rather than blanking it.
    /// </summary>
    public string? DisplayName { get; set; }
}

/// <summary>For operations with nothing to return.</summary>
public sealed record MarkedResultDto(bool Success)
{
    public static readonly MarkedResultDto Done = new(true);
}
