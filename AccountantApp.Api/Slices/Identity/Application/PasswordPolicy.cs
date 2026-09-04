using AccountantApp.Api.Shared.Errors;

namespace AccountantApp.Api.Slices.Identity.Application;

/// <summary>
/// One place, called by every handler that accepts a new password. Violations are 422 with a message
/// naming the rule that failed -- never 500, because this is a client-supplied value.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 12;

    /// <summary>
    /// Not cosmetic: PBKDF2 hashes whatever it is given, so an endpoint accepting a 10 MB password
    /// is an endpoint that burns CPU on request. Enforced BEFORE hashing.
    /// </summary>
    public const int MaximumLength = 128;

    /// <summary>
    /// Deliberately no composition rules -- no required uppercase, digit or symbol. This follows
    /// NIST SP 800-63B: composition rules push people toward "Password1!" and add far less entropy
    /// than length. Do not add them because they look more secure.
    /// </summary>
    public static void Validate(string? password, string loginEmail)
    {
        if (string.IsNullOrEmpty(password))
            throw new AppException("A password is required.", 422);

        if (password.Length < MinimumLength)
            throw new AppException(
                $"The password must be at least {MinimumLength} characters long.", 422);

        if (password.Length > MaximumLength)
            throw new AppException(
                $"The password must be at most {MaximumLength} characters long.", 422);

        if (string.Equals(password.Trim(), loginEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new AppException("The password must not be the same as the login email.", 422);
    }
}
