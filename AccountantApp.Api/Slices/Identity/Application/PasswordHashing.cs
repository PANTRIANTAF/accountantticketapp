using AccountantApp.Api.Slices.Identity.Core;
using Microsoft.AspNetCore.Identity;

namespace AccountantApp.Api.Slices.Identity.Application;

public enum PasswordVerification
{
    Failed,
    Success,
    SuccessRehashNeeded
}

public interface IPasswordHashing
{
    string Hash(string password);

    /// <summary>
    /// Verifies, and reports whether the stored hash used an older format and should be rewritten.
    /// Returns Failed for a null or empty stored hash -- never true -- but only after doing the same
    /// work a real verification would. See the implementation.
    /// </summary>
    PasswordVerification Verify(string? storedHash, string password);
}

/// <summary>
/// PasswordHasher&lt;T&gt; only, from Microsoft.AspNetCore.Identity: PBKDF2-HMAC-SHA512 at 210,000
/// iterations with a self-describing versioned format. Deliberately no UserManager, SignInManager,
/// IdentityOptions or AddIdentity() -- they own the whole account lifecycle, which this slice
/// specifies differently, and half-adopting them produces two lockout implementations that disagree.
/// Registered as a singleton: the hasher is stateless and thread-safe.
/// </summary>
public sealed class PasswordHashing : IPasswordHashing
{
    private readonly PasswordHasher<UserAccount> _hasher = new();

    /// <summary>
    /// Computed ONCE, because this type is a singleton. It exists so that the no-account and
    /// Invited-account paths cost the same as a real verification.
    /// </summary>
    private readonly string _dummyHash;

    public PasswordHashing()
    {
        _dummyHash = _hasher.HashPassword(null!, "timing-defence-dummy-password");
    }

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public PasswordVerification Verify(string? storedHash, string password)
    {
        // An Invited account has a null hash, and so does "no such account" in LoginHandler.
        //
        // Passing null into the hasher throws, and a 500 on the login path leaks that the account
        // exists in that state. But an early `return Failed` is equally wrong, for a reason that is
        // invisible locally: it makes this path return in microseconds while a real account costs
        // ~100ms of PBKDF2, and that difference is measurable over the network. It is a working
        // account-enumeration oracle against every email address the Office holds.
        //
        // So do the work, discard it, and report Failed. Do not "optimise" this away -- there is a
        // test for the timing property, and it is checking security, not performance.
        if (string.IsNullOrEmpty(storedHash))
        {
            _hasher.VerifyHashedPassword(null!, _dummyHash, password);
            return PasswordVerification.Failed;
        }

        return _hasher.VerifyHashedPassword(null!, storedHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordVerification.Success,
            // The stored hash used fewer iterations or an older format. The caller MUST rewrite it;
            // ignoring this means the upgrade never happens and the return value is decoration.
            PasswordVerificationResult.SuccessRehashNeeded => PasswordVerification.SuccessRehashNeeded,
            _ => PasswordVerification.Failed
        };
    }

    // Never log a password, a hash, or any prefix of either, at any level, including Debug.
}
