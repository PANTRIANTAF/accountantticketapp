namespace AccountantApp.Api.Slices.Identity.Core;

/// <summary>
/// An invitation or password-reset token. Only the SHA-256 hash is stored: the raw token exists in
/// the body of exactly one email and nowhere else. If you find yourself storing the raw token
/// anywhere, stop -- the whole mechanism is undone and nothing will fail a test to tell you.
/// </summary>
public sealed class UserAccountToken
{
    public Guid Id { get; set; }
    public Guid UserAccountId { get; set; }
    public string Purpose { get; set; } = string.Empty;

    /// <summary>Lowercase hex SHA-256 of the raw token. Always 64 characters.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// "Unconsumed AND unexpired", written once. Two separate checks at four call sites is four
    /// chances to forget the second one, and forgetting it means an expired token still works.
    /// </summary>
    public bool IsRedeemable(DateTimeOffset now) =>
        ConsumedAt is null && ExpiresAt > now;
}

public static class TokenPurpose
{
    public const string Invitation = "Invitation";
    public const string PasswordReset = "PasswordReset";

    /// <summary>Invitations wait on a human; a reset answers something the person just did.</summary>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);
}
