import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type {
  AcceptInvitationRequest,
  AccountantDetail,
  AccountantSummary,
  ChangePasswordRequest,
  CompletePasswordResetRequest,
  InviteAccountantRequest,
  LoginRequest,
  MarkedResult,
  RequestPasswordResetRequest,
  SessionDto,
} from './types';

/**
 * One function per endpoint, named for the endpoint, verified against
 * AccountantApp.Api/Slices/Identity/IdentityEndpoints.cs:
 *
 *   login                  POST /api/auth/login                     :32   SessionDto, 401, 422
 *   requestPasswordReset   POST /api/auth/request-password-reset     :42   200 ONLY
 *   completePasswordReset  POST /api/auth/complete-password-reset    :50   400, 422
 *   acceptInvitation       POST /api/auth/accept-invitation          :59   400, 422
 *   logout                 POST /api/auth/logout                    :69   401
 *   changeOwnPassword      POST /api/auth/change-password            :83   401, 422
 *
 * /api/auth/me is the ONE GET in the group (:76) and is called by SessionProvider, not from here --
 * see the note on SessionDto in types.ts.
 *
 * THIS FILE CONTAINS NO REACT, NO HOOKS AND NO TANSTACK QUERY (GeneralUIArchitecture.md section 2.5),
 * so it can be read against the C# endpoint file line by line. Cache behaviour is in queries.ts.
 */

/**
 * Returns SessionDto and the Set-Cookie header. There is no token in the body and nothing to store:
 * the cookie is the session and it is HttpOnly, so the client cannot read it and must not try.
 *
 * ONE 401 WITH ONE MESSAGE -- "Invalid email or password." (LoginHandler.cs:38) -- FOR SIX DISTINCT
 * CAUSES: no such account, wrong password, account still Invited, account Suspended, account locked
 * out, and the owning Customer suspended. The handler requires the response to be byte-for-byte
 * identical for all of them, because any distinction answers "does this address have an account
 * here". The real reason is in the audit log and only there.
 */
export function login(request: LoginRequest): Promise<SessionDto> {
  return post<SessionDto>('/api/auth/login', request);
}

/**
 * RETURNS 200 UNCONDITIONALLY. IdentityEndpoints.cs:42 declares .Produces<MarkedResultDto>() and
 * nothing else -- no 404 and no 422 -- because an unknown address must get the same answer as a known
 * one. The handler does not even validate the address format: a 422 for a malformed address and a 200
 * for a well-formed unknown one is the same oracle, just quieter.
 */
export function requestPasswordReset(request: RequestPasswordResetRequest): Promise<MarkedResult> {
  return post<MarkedResult>('/api/auth/request-password-reset', request);
}

/**
 * EVERY FAILURE IS ONE 400 WITH ONE MESSAGE -- "That link is invalid or has expired."
 * (CompletePasswordResetHandler.cs:16) -- covering no such token, wrong purpose, already consumed,
 * expired, and the account being suspended between the request and the click.
 *
 * IT DOES NOT SIGN THE USER IN. The handler's comment is explicit: a leaked reset link must not grant
 * a live session in one step. It also clears the lockout along with the password.
 */
export function completePasswordReset(
  request: CompletePasswordResetRequest,
): Promise<MarkedResult> {
  return post<MarkedResult>('/api/auth/complete-password-reset', request);
}

/**
 * One opaque 400 for every failure -- "That invitation is invalid or has expired."
 * (AcceptInvitationHandler.cs:17). No session on success, same as the reset.
 *
 * All three invitation producers -- /api/accountants/invite, /api/employees/invite and
 * /api/customers/onboard -- land the invitee here with the same token purpose, so the caller is
 * role-agnostic and CANNOT guess who it serves: the token is opaque and the caller is anonymous.
 */
export function acceptInvitation(request: AcceptInvitationRequest): Promise<MarkedResult> {
  return post<MarkedResult>('/api/auth/accept-invitation', request);
}

/**
 * No body. Returns { success: true }.
 *
 * There is NO SESSIONS TABLE -- the cookie IS the session -- so SignOutAsync only queues a Set-Cookie
 * that clears it. Nothing can fail halfway and leave a session alive on the server, and logging out
 * twice is a 200 both times.
 */
export function logout(): Promise<MarkedResult> {
  return post<MarkedResult>('/api/auth/logout');
}

/**
 * Returns MarkedResultDto -- NOT a SessionDto. The handler re-issues the cookie with the
 * must-change-password flag cleared, so the session must be INVALIDATED rather than seeded; see
 * queries.ts.
 *
 * TWO ORDERING FACTS. The handler validates the NEW password BEFORE verifying the current one
 * (ChangeOwnPasswordHandler.cs:65, deliberately), so a 6-character new password is reported as such
 * rather than producing a 401 the user reads as "I got my old password wrong". And A WRONG CURRENT
 * PASSWORD IS 401, NOT 403 (:88) -- a failed credential check, which does not increment the lockout
 * counter and cannot lock the account, so do not warn that it might.
 */
export function changeOwnPassword(request: ChangePasswordRequest): Promise<MarkedResult> {
  return post<MarkedResult>('/api/auth/change-password', request);
}

/* ==================================================================================================
 * ACCOUNTANT MANAGEMENT -- the six routes of MapAccountantEndpoints (IdentityEndpoints.cs:92-162).
 *
 * Still no React, no hooks and no TanStack Query, so this half also reads against the C# endpoint file
 * line by line. Still no headers, no `credentials`, no base URL and no import.meta.env either:
 * http.ts owns all of it, there is no VITE_API_URL and there never will be, and CORS is never
 * configured anywhere (04-Infrastructure.md sections 1-3).
 * ================================================================================================ */

/**
 * GET /api/accountants/list -- IdentityEndpoints.cs:108-121. AccountantAdmin and AccountantUser.
 *
 * THE UNION RETURN TYPE IS THE POINT OF THIS SIGNATURE, and it is the most important line in the
 * file. ListAccountantsHandler.cs:77-85 returns PaginatedResponse<AccountantDetailDto> for an
 * AccountantAdmin and :88-95 returns PaginatedResponse<AccountantSummaryDto> for an AccountantUser.
 * The route's `.Produces<PaginatedResponse<AccountantDetailDto>>()` at :120 documents the richer
 * shape for BOTH callers and :116-119 warns in so many words that it "must not be used to infer the
 * response shape for a non-Admin caller" -- so it is actively misleading, this hand-written union is
 * the contract until BACKEND_CHANGES_REQUIRED item 6 is answered, and a generated client would be
 * wrong in exactly this place (item 9).
 *
 * Do NOT widen it to the detail type because the Admin case is the interesting one, and do not add a
 * generic that lets the caller choose: an un-narrowed caller reading `.status` MUST fail to compile,
 * and that compile error is the entire mechanism. The narrowing happens once, in
 * AccountantListScreen, against `session.role` -- the same discriminator the server uses.
 *
 * The query string is built from NUMBERS ONLY. Both parameters are `int?`, defaulted in the lambda
 * (:112-113) before PaginatedQuery.Normalize clamps them to [1,50]; a non-numeric value is a bare
 * model-binding 400 with nothing useful in it. Sending a pageSize above 50 is a 200 carrying 50
 * (BACKEND_CHANGES_REQUIRED item 17) -- this file may send what it is given, and the pager is
 * rendered from response.pageSize.
 */
export function listAccountants(params: {
  pageNumber: number;
  pageSize: number;
}): Promise<PaginatedResponse<AccountantDetail> | PaginatedResponse<AccountantSummary>> {
  const query = new URLSearchParams({
    pageNumber: String(params.pageNumber),
    pageSize: String(params.pageSize),
  });
  return get(`/api/accountants/list?${query.toString()}`);
}

/**
 * POST /api/accountants/invite -- :96-106. AccountantAdmin only.
 *
 * RETURNS 201, NOT 200, with `Location: /api/accountants/list` (:100) -- the list, not the new row.
 * http.ts branches on `response.ok`, so 201 is success and needs no special case; never follow the
 * header, because there is no detail route to follow it to. The 201 body is an AccountantDetailDto and
 * it CARRIES NO TOKEN: InviteAccountantHandler.cs:134-142 puts the raw invitation token in the
 * notification's EmailBody only. There is nothing here to copy, display or log.
 *
 * 409 "An account already exists for '<normalised email>'." is the duplicate
 * (InviteAccountantHandler.cs:86), and a second invite to the same address is that 409 whatever the
 * target's status -- so there is no resend to wrap.
 */
export function inviteAccountant(body: InviteAccountantRequest): Promise<AccountantDetail> {
  return post<AccountantDetail>('/api/accountants/invite', body);
}

/*
 * The four row actions: POST, AccountantAdmin only, all four returning the FULL AccountantDetailDto
 * (IdentityMapper.cs:14-21), which is what queries.ts patches into the cached pages.
 *
 * A. EACH TAKES A BARE string AND BUILDS { userAccountId } HERE, so the key AccountantDtos.cs:51
 *    requires is written once, beside the endpoint, rather than four times at four call sites.
 * B. FOUR ONE-LINE FUNCTIONS, NOT ONE accountantAction(action, id) HELPER. A helper that interpolates
 *    the route hides four operations with four distinct precondition sets and four distinct 422
 *    messages behind one name, and none of those messages is then traceable to a call site.
 * C. There is no get-single, no update and no delete route to wrap. 02-AuthorizationMatrix.md
 *    section 2: "Delete an Accountant account -- Nobody. Suspension only."
 */

/** :125-134. 404 not found; 422 self / already suspended / last active Accountant Admin. */
export function suspendAccountant(userAccountId: string): Promise<AccountantDetail> {
  return post<AccountantDetail>('/api/accountants/suspend', { userAccountId });
}

/** :136-143. 404; 422 invitation not accepted / already active. No self guard, deliberately. */
export function reactivateAccountant(userAccountId: string): Promise<AccountantDetail> {
  return post<AccountantDetail>('/api/accountants/reactivate', { userAccountId });
}

/** :145-152. 404; 422 already an Accountant Admin. Allowed on Invited and Suspended rows. */
export function promoteAccountant(userAccountId: string): Promise<AccountantDetail> {
  return post<AccountantDetail>('/api/accountants/promote', { userAccountId });
}

/** :154-161. 404; 422 self / not an Accountant Admin / last active Accountant Admin. */
export function demoteAccountant(userAccountId: string): Promise<AccountantDetail> {
  return post<AccountantDetail>('/api/accountants/demote', { userAccountId });
}
