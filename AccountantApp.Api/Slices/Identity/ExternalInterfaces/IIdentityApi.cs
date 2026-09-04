using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Slices.Identity.ExternalInterfaces;

/// <summary>
/// What other slices see of a UserAccount. Never the entity itself: a caller holding a tracked
/// UserAccount could mutate it and save it through another slice's context, which is the exact
/// coupling one-DbContext-per-slice exists to prevent.
///
/// No PasswordHash, no lockout state, no failed-login count, no timestamps. Nothing outside this slice
/// has a use for them, and a contract that carries a hash makes every consumer a leak path.
/// </summary>
public sealed record AccountSummary(
    Guid Id,
    string DisplayName,
    string LoginEmail,
    UserRole Role,
    string Status)
{
    public bool IsActive => Status == "Active";
}

/// <summary>
/// Creating an Employee's account. Both ids are REQUIRED and non-nullable: Employees knows the
/// Employee and its owning Customer, Identity can look up neither, and ck_user_accounts_scope rejects
/// the row if either is missing.
///
/// Do not add an overload that omits CustomerId. It would compile, insert nothing, and fail with a
/// check-constraint violation at a call site with no way to see why.
/// </summary>
public sealed record InviteEmployeeAccount(
    Guid EmployeeId,
    Guid CustomerId,
    string LoginEmail,
    string DisplayName,
    // CustomerAdmin or Employee only. An Accountant role throws InvalidOperationException -- that is a
    // programming error in the calling slice, not something a user did.
    UserRole Role);

public interface IIdentityApi
{
    // --- Reads, for Tickets and Employees ---

    /// <summary>Null when no such account exists.</summary>
    Task<AccountSummary?> FindAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>
    /// Bulk lookup for list rendering, so callers do not run a query per row. Missing ids are simply
    /// absent from the dictionary. Capped at 500 ids.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, AccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> userAccountIds, CancellationToken ct = default);

    /// <summary>
    /// True only when the account exists AND is Active. This is what the pickup queue asks to decide
    /// whether an assignment is stranded. Answered live -- a status change is exactly the event a cache
    /// would hide.
    /// </summary>
    Task<bool> IsActiveAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>The account belonging to an Employee, or null for an accountless one.</summary>
    Task<AccountSummary?> FindByEmployeeAsync(Guid employeeId, CancellationToken ct = default);

    /// <summary>
    /// Every Accountant of either role, for another slice's assignee picker. Unpaginated, and
    /// deliberately NOT the same return type as ListAccountantsHandler: that endpoint strips fields by
    /// role, and sharing a type would make the stripping something a caller could bypass.
    /// </summary>
    Task<IReadOnlyList<AccountSummary>> ListAccountantsAsync(
        bool activeOnly = true, CancellationToken ct = default);

    // --- Writes, for Employees only ---
    //
    // All write methods ENLIST in the caller's transaction and never open or commit one of their own.
    // Employees' composite onboarding depends on it: a failure after the account is created must leave
    // no account behind.
    //
    // None of them check permissions. The calling handler has already called RequireAsync with its own
    // action name, and the role rule for Employee accounts -- which includes a CustomerAdmin acting
    // within their own Customer -- needs the Customer scope that only Employees knows. Identity enforces
    // STRUCTURAL invariants here, never role rules.
    //
    // All of them DO audit, because the account is this slice's data.

    /// <summary>
    /// Creates an Invited account for an Employee, issues an invitation token, and queues the
    /// invitation email -- all in the caller's transaction. Returns the new account id.
    /// </summary>
    Task<Guid> InviteEmployeeAccountAsync(
        InviteEmployeeAccount request, CancellationToken ct = default);

    /// <summary>
    /// IDEMPOTENT: suspending an already-suspended account is a no-op, not an error.
    ///
    /// This differs on purpose from the HTTP endpoint, which returns 422 for the same input, because the
    /// two callers ask different questions. A human clicking Suspend on a suspended account has made a
    /// mistake and should be told. A departure handler is asserting an END STATE, and a departing
    /// Employee whose account was already suspended for an unrelated reason is an ordinary case -- if
    /// this threw, that departure could not be recorded at all.
    ///
    /// Do not implement this by calling the handler, and do not unify the two.
    /// </summary>
    Task SuspendAccountAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>
    /// Idempotent in the same way, and for the same reason.
    ///
    /// Restores the account to the state it can be USED in, not unconditionally to Active: an account with
    /// no password hash goes back to Invited, because it was a never-accepted invitee before it was
    /// suspended and an Active account with no password can never be logged into or re-invited.
    /// </summary>
    Task ReactivateAccountAsync(Guid userAccountId, CancellationToken ct = default);

    /// <summary>
    /// Changes the address a Customer-side account signs in with. Authorized in Employees, and reserved
    /// there to the two Accountant roles -- the Office changes it on request, because a self-service
    /// login-email change is an account-takeover route and a Customer Admin changing a colleague's is the
    /// same thing one step removed.
    ///
    /// 409 when another account already uses the address; that is a user-facing conflict, not a bug.
    /// Throws InvalidOperationException for an Accountant target, like SetCustomerSideRoleAsync: no
    /// Customer-side actor may modify an Accountant account, and this method is a way round that rule.
    ///
    /// It does NOT touch the password, the session, or EmailConfirmedAt. The person keeps their password
    /// and any live session; the next login uses the new address.
    /// </summary>
    Task ChangeLoginEmailAsync(
        Guid userAccountId, string loginEmail, CancellationToken ct = default);

    /// <summary>
    /// Sets a Customer-side account's role. Throws when the TARGET is an Accountant account and when
    /// the requested ROLE is an Accountant role -- both directions, because the two mistakes are
    /// different: the first is a wrong id, the second a wrong role. No Customer-side actor may create or
    /// modify an Accountant account, and this method is how that rule could be circumvented.
    /// </summary>
    Task SetCustomerSideRoleAsync(
        Guid userAccountId, UserRole role, CancellationToken ct = default);
}
