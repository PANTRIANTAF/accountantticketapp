/**
 * Request and response shapes for BOTH halves of this slice, verified field by field against
 * AccountantApp.Api/Slices/Identity/Application/Dtos/AuthDtos.cs (the /api/auth/* group, first) and
 * .../Dtos/AccountantDtos.cs (the /api/accountants/* group, from ACCOUNTANT MANAGEMENT below).
 *
 * IdentityEndpoints.cs registers TWO MapGroup prefixes from one file -- /api/auth at :28 and
 * /api/accountants at :94 -- because authentication and Office administration are one slice's two
 * jobs. Nothing above the ACCOUNTANT MANAGEMENT banner belongs to the accountant list, and nothing
 * below it belongs to a password form.
 */
import type { AccountStatus, UserRole } from '../../shared/format/enums';

/**
 * SessionDto is DECLARED IN shared/auth/SessionProvider.tsx and RE-EXPORTED here.
 *
 * It is not redeclared. GeneralUIArchitecture.md section 1.4 rule A says shared/ may never import
 * from slices/, and SessionProvider is the thing that bootstraps GET /api/auth/me, so the type has to
 * live there. Two declarations of the session shape is exactly the drift rule A exists to prevent --
 * and the field that would rot first is `role`, which is a NUMBER whose zero value is the most
 * privileged role in the system.
 */
export type { SessionDto } from '../../shared/auth/SessionProvider';

/** LoginRequestDto. */
export interface LoginRequest {
  email: string;
  password: string;
}

/**
 * ChangePasswordRequestDto. NOTE THERE IS NO TARGET USER FIELD, deliberately, so the endpoint cannot
 * be pointed at another account even by mistake. 02-AuthorizationMatrix.md section 11: resetting
 * another person's password directly is permitted to NOBODY. There is no administrative password
 * reset to build, for any role.
 */
export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

/** RequestPasswordResetRequestDto. */
export interface RequestPasswordResetRequest {
  email: string;
}

/** CompletePasswordResetRequestDto. */
export interface CompletePasswordResetRequest {
  token: string;
  newPassword: string;
}

/**
 * AcceptInvitationRequestDto. `displayName` is THE ONLY OPTIONAL FIELD in this group.
 *
 * Absent means "keep what the inviter typed". Send it ONLY when the user typed something -- never
 * `''` (GeneralUIArchitecture.md section 9.3 rule F). AcceptInvitationHandler happens to treat blank
 * as absent, so sending `''` breaks nothing TODAY, which is precisely why the habit survives to a
 * field where it does.
 */
export interface AcceptInvitationRequest {
  token: string;
  newPassword: string;
  displayName?: string;
}

/** MarkedResultDto(bool Success) -- what logout and change-password return. NOT a SessionDto. */
export interface MarkedResult {
  success: boolean;
}

/**
 * The password policy, mirrored from
 * AccountantApp.Api/Slices/Identity/Application/PasswordPolicy.cs (read in full) and
 * Handlers/ChangeOwnPasswordHandler.cs. Every rule is a 422 server-side.
 *
 *   Required                          non-empty            PasswordPolicy.cs
 *   Minimum length                    12                   PasswordPolicy.cs:11
 *   Maximum length                    128                  PasswordPolicy.cs:17
 *   Not equal to the login email      trimmed, ci          PasswordPolicy.cs:37-38
 *   Different from the current one    exact, cs            ChangeOwnPasswordHandler.cs:92
 *
 * THE FIFTH RULE IS NOT IN PasswordPolicy. LoginArchitecture.md section 3.4 attributes all five to
 * PasswordPolicy.Validate; only four are there. The fifth is raised in the handler AFTER the policy
 * call, and that placement is not an accident -- PasswordPolicy is given a password and a login email
 * and has no idea what the current password is, so only the change-password path can enforce it. All
 * five are enforced and all five are 422; a builder who opens PasswordPolicy.cs looking for the fifth
 * will not find it and must not conclude it does not exist.
 *
 * THE FIFTH RULE THEREFORE APPLIES TO CHANGE-PASSWORD ONLY, not to reset or invitation acceptance:
 * neither has a current password to compare against, and mirroring it there would reject a user who
 * legitimately reuses the password they could not remember well enough to sign in with.
 *
 * THE FOURTH RULE CANNOT BE CHECKED CLIENT-SIDE AT ALL. SessionDto carries no loginEmail, so the
 * comparison happens server-side only and surfaces as a 422 banner. BACKEND_CHANGES_REQUIRED item 11.
 *
 * AND THERE ARE DELIBERATELY NO COMPOSITION RULES -- no required uppercase, digit or symbol,
 * following NIST SP 800-63B, and PasswordPolicy.cs:19-23 says so in a comment. DO NOT ADD THEM
 * because they look more secure: a client rule the server does not enforce rejects passwords the
 * server would have accepted, and the user cannot discover which rule is imaginary.
 */
export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 128;

/** The message for both length rules, so all three password forms word them identically. */
export const PASSWORD_LENGTH_MESSAGE = `Use at least ${String(PASSWORD_MIN_LENGTH)} characters.`;
export const PASSWORD_TOO_LONG_MESSAGE = `Use at most ${String(PASSWORD_MAX_LENGTH)} characters.`;

/**
 * AcceptInvitationHandler.cs:20, DisplayNameMaximumLength = 200; the 422 is at :84-86.
 * THIS DIFFERS FROM THE 255 used for most display names elsewhere in the API.
 */
export const DISPLAY_NAME_MAX_LENGTH = 200;

/* ==================================================================================================
 * ACCOUNTANT MANAGEMENT -- /api/accountants/*
 *
 * Six routes, verified against IdentityEndpoints.cs:92-162 (MapAccountantEndpoints):
 *
 *   GET  /api/accountants/list        ?pageNumber&pageSize, both int?, both omissible  AA + AU
 *   POST /api/accountants/invite      { email, displayName, role }  ->  201            AA
 *   POST /api/accountants/suspend     { userAccountId }             ->  200            AA
 *   POST /api/accountants/reactivate  { userAccountId }             ->  200            AA
 *   POST /api/accountants/promote     { userAccountId }             ->  200            AA
 *   POST /api/accountants/demote      { userAccountId }             ->  200            AA
 *
 * `list` IS THE GET AND THE OTHER FIVE ARE POST. The `list` suffix predicts the verb nowhere else in
 * this API, and "correcting" either is a 405.
 * ================================================================================================ */

/**
 * Mirrors AccountantSummaryDto -- Application/Dtos/AccountantDtos.cs:30,
 * `public sealed record AccountantSummaryDto(Guid Id, string DisplayName)`. TWO FIELDS, and that is
 * normative rather than minimal: 02-AuthorizationMatrix.md section 2 -- "Return names and identifiers
 * only -- not email addresses, login history, or status detail."
 *
 * WHAT AN AccountantUser RECEIVES FROM /list. ListAccountantsHandler.cs:43 is declared
 * `Task<object>` and branches at :77 on `user.Role == UserRole.AccountantAdmin`; System.Text.Json
 * serialises the RUNTIME type, so for an AccountantUser the other five keys are ABSENT FROM THE JSON
 * -- not null, not empty: absent.
 */
export interface AccountantSummary {
  /** A C# Guid; a lowercase hyphenated string on the wire, never a Guid object. */
  id: string;
  displayName: string;
}

/**
 * Mirrors AccountantDetailDto -- AccountantDtos.cs:33-40. RETURNED TO AccountantAdmin ONLY: from
 * /list when the caller is an Admin, and as the full body of all four row actions
 * (IdentityMapper.cs:14-21), which is what makes the cache patch in queries.ts possible.
 *
 * IT EXTENDS THE SUMMARY RATHER THAN BEING ONE INTERFACE WITH FIVE OPTIONAL FIELDS. `loginEmail?:
 * string` compiles, reads as tolerant, and destroys the compile error the union return type of
 * listAccountants() exists to produce -- AccountantDtos.cs:26-28 gives the server-side version of the
 * same argument: "a type that has no LoginEmail property cannot leak one, whereas a handler that must
 * remember to null it out will one day forget."
 */
export interface AccountantDetail extends AccountantSummary {
  loginEmail: string;
  /**
   * A NUMBER on the wire, 0-3. No JsonStringEnumConverter is registered anywhere
   * (BACKEND_CHANGES_REQUIRED item 4), so AccountantAdmin arrives as 0 -- which is FALSY. Compare
   * with === against UserRole; never test it for truthiness.
   */
  role: UserRole;
  /** A STRING on the wire, in the same row as the number above. Never Number(status). */
  status: AccountantStatus;
  /** C# DateTimeOffset: carries an offset. Format through shared/format/dates.ts. */
  createdAt: string;
  /** null for anyone who has never signed in -- render an em dash, not "Invalid Date". */
  lastLoginAt: string | null;
}

/**
 * UserAccount.Status -- Core/UserAccount.cs:77-82, `Invited | Active | Suspended`. NOT the Customer
 * vocabulary (`Active | Suspended`, and a Customer is never Invited) and not the Employee one
 * (`Active | Departed`).
 *
 * AN ALIAS, NOT A SECOND DECLARATION. The plan's step 1 spells the union out here, but
 * GeneralUIArchitecture.md section 10.1 and section 1.2 put all four status vocabularies in
 * shared/format/enums.ts, StatusChip's StatusWord is built from that one, and section 3 outranks the
 * plan. Two structurally identical unions would be two things to keep in step with one CHECK
 * constraint, with no compile error when they drift.
 */
export type AccountantStatus = AccountStatus;

/**
 * Mirrors InviteAccountantRequestDto -- AccountantDtos.cs:6-18.
 *
 * `role` is sent as a JSON NUMBER. `{"role":"1"}` is a 400 from model binding before
 * InviteAccountantHandler runs, so the banner names no field. And only two of the four values are
 * legal here: InviteAccountantHandler.cs:58-60 answers 422 "An invited accountant must be an
 * Accountant Admin or an Accountant User." for the other two. The narrowing to those two lives in
 * screens/inviteAccountantSchema.ts, where the rest of the field limits are.
 */
export interface InviteAccountantRequest {
  email: string;
  displayName: string;
  role: UserRole;
}

/**
 * Mirrors AccountIdRequestDto -- AccountantDtos.cs:49-52, shared by suspend, reactivate, promote and
 * demote.
 *
 * THE KEY IS `userAccountId`. Not `id`, not `accountantId`: the DTO has that one property, so
 * `{ id }` binds Guid.Empty and returns 404 "Accountant not found." for a row visibly on screen,
 * which reads as a stale list rather than as a typo. api.ts builds this object in one place so the
 * name is written once, beside the endpoint.
 */
export interface AccountIdRequest {
  userAccountId: string;
}
