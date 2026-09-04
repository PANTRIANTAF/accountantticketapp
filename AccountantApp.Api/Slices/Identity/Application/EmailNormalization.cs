using System.Net.Mail;
using AccountantApp.Api.Shared.Errors;

namespace AccountantApp.Api.Slices.Identity.Application;

/// <summary>
/// "Alice@Example.COM" and "alice@example.com" are the same mailbox for every practical purpose, and
/// a system that lets both exist as separate logins has two accounts for one person -- one of which
/// will be the one nobody remembers using.
/// </summary>
public static class EmailNormalization
{
    public const int MaximumLength = 320;

    /// <summary>
    /// Normalize on write and query the normalized column. Do NOT implement this as .ToLower()
    /// inside a Where clause on login_email: that is unindexable, and the pattern is already
    /// forbidden elsewhere in this codebase.
    /// </summary>
    public static string Normalize(string? email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Accepts the address if it parses, has exactly one '@' with something on both sides, and is
    /// 320 characters or fewer. Deliberately not a regular expression: an over-clever pattern
    /// rejects legitimate addresses, and the invitation email is the real validator -- an address
    /// that cannot receive the invitation never becomes an account.
    /// </summary>
    public static string Require(string? email)
    {
        var trimmed = (email ?? string.Empty).Trim();

        if (trimmed.Length == 0)
            throw new AppException("An email address is required.", 422);
        if (trimmed.Length > MaximumLength)
            throw new AppException(
                $"The email address must be at most {MaximumLength} characters long.", 422);
        if (trimmed.Count(character => character == '@') != 1
            || trimmed.StartsWith('@') || trimmed.EndsWith('@'))
            throw new AppException("That email address is not valid.", 422);

        try
        {
            _ = new MailAddress(trimmed);
        }
        catch (FormatException)
        {
            throw new AppException("That email address is not valid.", 422);
        }

        return trimmed;
    }
}
