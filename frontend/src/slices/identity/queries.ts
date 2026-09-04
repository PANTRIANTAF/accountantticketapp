import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import type { PaginatedResponse } from '../../shared/api/paginated';
import { SESSION_QUERY_KEY, type SessionDto } from '../../shared/auth/SessionProvider';
import {
  usePaginatedQuery,
  type UsePaginatedQueryResult,
} from '../../shared/hooks/usePaginatedQuery';
import {
  acceptInvitation,
  changeOwnPassword,
  completePasswordReset,
  demoteAccountant,
  inviteAccountant,
  listAccountants,
  login,
  logout,
  promoteAccountant,
  reactivateAccountant,
  requestPasswordReset,
  suspendAccountant,
} from './api';
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
} from './types';

/**
 * THREE MUTATIONS, THREE DIFFERENT CACHE BEHAVIOURS. Getting one wrong looks like a server bug.
 *
 *   useLogin              returns SessionDto     SEEDS ['identity','session']
 *   useChangeOwnPassword  returns MarkedResult   INVALIDATES ['identity','session']
 *   useLogout             returns MarkedResult   queryClient.clear() -- the WHOLE cache
 *
 * The other three mutations here touch no cache at all: they are anonymous calls that create no
 * session (see useCompletePasswordReset).
 */

/**
 * SEEDS the session query with the response (GeneralUIArchitecture.md section 3.2 rule D).
 * Invalidating instead would throw away a SessionDto already in hand and immediately re-request it.
 *
 * The CALLER decides where to go, because the decision has three inputs and one of them is
 * security-relevant: mustChangePassword first, then a validated return-to path, then the role's
 * landing route. See LoginScreen.
 */
export function useLogin() {
  const queryClient = useQueryClient();

  return useMutation<SessionDto, Error, LoginRequest>({
    mutationFn: login,
    onSuccess: (session) => {
      queryClient.setQueryData(SESSION_QUERY_KEY, session);
    },
  });
}

/** Fire and forget: the response is a 200 whatever happens, so there is nothing to branch on. */
export function useRequestPasswordReset() {
  return useMutation<MarkedResult, Error, RequestPasswordResetRequest>({
    mutationFn: requestPasswordReset,
  });
}

/**
 * NO CACHE WRITE, ON PURPOSE. Completing a reset does NOT sign the user in -- a leaked reset link must
 * not grant a live session in one step. Do not seed the session and do not call /api/auth/me hoping
 * for one: there is none, and the 401 is the bootstrap path so it would simply settle the session as
 * anonymous while the screen waited for something that is never coming.
 */
export function useCompletePasswordReset() {
  return useMutation<MarkedResult, Error, CompletePasswordResetRequest>({
    mutationFn: completePasswordReset,
  });
}

/** Same as the reset: no session on success, redirect to /login. */
export function useAcceptInvitation() {
  return useMutation<MarkedResult, Error, AcceptInvitationRequest>({
    mutationFn: acceptInvitation,
  });
}

/**
 * INVALIDATES the session; it does NOT seed it.
 *
 * The endpoint returns MarkedResultDto, so seeding would write `{ success: true }` over the session
 * and the app would no longer know who is logged in. The handler re-issues the cookie with the
 * must-change-password flag cleared, so the next /api/auth/me returns false. Skipping the
 * invalidation leaves the stale `true` and RequireSession returns the user to /change-password
 * forever, having already succeeded.
 */
export function useChangeOwnPassword() {
  const queryClient = useQueryClient();

  return useMutation<MarkedResult, Error, ChangePasswordRequest>({
    mutationFn: changeOwnPassword,
    // RETURNED, NOT FIRE-AND-FORGET. TanStack awaits a promise returned from onSuccess before it runs
    // the caller's own onSuccess, so the screen navigates only once /api/auth/me has come back with
    // mustChangePassword: false. Dropping the `return` reintroduces the trap this invalidation exists
    // to avoid, one step later: the screen navigates while the cached flag is still `true`, so
    // RequireSession bounces the user straight back to a freshly mounted, empty change-password form
    // they have already completed successfully.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: SESSION_QUERY_KEY }),
  });
}

/**
 * A. CLEARS THE ENTIRE CACHE, not just the session key. Leaving customer lists, employee records and
 *    audit entries in memory means the next user at the same browser sees the previous user's data
 *    flash on screen before their own requests resolve. On a shared office machine that is a real
 *    disclosure.
 * B. NAVIGATES TO /login WITH `replace`, so the back button does not return to an authenticated route
 *    that will now 401.
 * C. LOGGING OUT TWICE IS A 200 BOTH TIMES. It is idempotent, so the button is not guarded against a
 *    double click with an error.
 * D. IF THE CALL ITSELF FAILS, CLEAR AND REDIRECT ANYWAY -- hence onSettled rather than onSuccess.
 *    The user asked to leave; a failed logout that leaves them looking at an authenticated shell is
 *    worse than a cookie that outlives its client state, and the cookie is rejected on its own
 *    schedule regardless.
 * E. LOGOUT IS PERMITTED WHILE mustChangePassword IS SET -- MustChangePasswordMiddleware allows
 *    /api/auth/logout by name -- which is why the button is on /change-password too.
 *
 * Clearing the cache leaves the mounted session query with no data, so it refetches /api/auth/me and
 * gets a 401. That path is EXEMPT from the global 401 handler, so a deliberate logout does not raise
 * the "your session ended" message meant for an expiry.
 */
export function useLogout() {
  const queryClient = useQueryClient();
  const navigate = useNavigate();

  return useMutation<MarkedResult, Error, void>({
    mutationFn: logout,
    onSettled: () => {
      queryClient.clear();
      void navigate('/login', { replace: true });
    },
  });
}

/* ==================================================================================================
 * ACCOUNTANT MANAGEMENT -- one key factory, one list hook, five mutation hooks.
 *
 * Screens import hooks from this file and NEVER api.ts (GeneralUIArchitecture.md section 3.2 rule A),
 * so every caching decision is here rather than in JSX.
 * ================================================================================================ */

/**
 * Keys follow [sliceName, resource, ...discriminators] (section 3.1).
 *
 * `lists` IS THE BLAST RADIUS, and it is three segments. IdentityScreens.md section 4.2 states it
 * exactly -- "The three-segment prefix ['identity', 'accountants', 'list'] is every mutation's blast
 * radius" -- and section 6 rule G's sample passes that array literally. The plan's step 3 writes
 * `accountantKeys.all` in the same sentence as "that three-segment prefix", which its own two-segment
 * definition is not; the screen spec outranks the plan, so both exist and the mutations use `lists`.
 *
 * ['identity', 'session'] IS NOT IN HERE. That key belongs to shared/auth/SessionProvider and is only
 * ever READ by this slice's accountant screens: demoting somebody else does not change YOUR session,
 * and nothing in this half re-fetches /api/auth/me.
 *
 * AND THERE IS NO `detail` KEY. There is no get-single route (IdentityEndpoints.cs:92-162 registers
 * six, none of them a read of one row), so a detail key would have no fetcher, would hold a row
 * forever, and no screen could read it.
 */
export const accountantKeys = {
  all: ['identity', 'accountants'] as const,
  lists: ['identity', 'accountants', 'list'] as const,
  list: (pageNumber: number, pageSize: number) =>
    ['identity', 'accountants', 'list', { pageNumber, pageSize }] as const,
};

/**
 * GET /api/accountants/list, for BOTH Accountant roles, returning api.ts's union unchanged --
 * narrowing is the screen's job and it narrows on `session.role`.
 *
 * A. THE PAGE PARAMETERS ARE IN THE KEY. Without them every page shares one cache entry and page 1's
 *    rows appear under page 3's pager, with no error anywhere (section 3.1).
 * B. NEVER `enabled: isAccountantAdmin`. Both Accountant roles may call this route
 *    (IdentityActionCatalogue.cs:24), and expressing a permission by disabling a query is forbidden
 *    (section 3.2 rule B): it would show an AccountantUser an empty table instead of the names
 *    02-AuthorizationMatrix.md section 2 entitles them to.
 * C. NO refetchInterval. The unread-notification count is the only polling query in the app
 *    (section 3.2 rule H).
 * D. It wraps usePaginatedQuery, like every paginated list in every slice (section 3.2 rule G), so
 *    the pageSize clamp is handled in exactly one place.
 *
 * The element type is the UNION of the two row shapes rather than the wide one, so
 * `data.items[0].status` does not compile until a caller has narrowed. PaginatedResponse<Detail> and
 * PaginatedResponse<Summary> are each assignable to this, which is why api.ts's union fits.
 */
export function useAccountantList(page: {
  pageNumber: number;
  pageSize: number;
}): UsePaginatedQueryResult<AccountantDetail | AccountantSummary> {
  return usePaginatedQuery<AccountantDetail | AccountantSummary>({
    queryKey: accountantKeys.list(page.pageNumber, page.pageSize),
    queryFn: (requested) => listAccountants(requested),
    pageNumber: page.pageNumber,
    pageSize: page.pageSize,
  });
}

/**
 * The shared onSuccess of the four row actions: PATCH THE ROW IN EVERY CACHED PAGE, THEN INVALIDATE.
 *
 * All four endpoints return the full AccountantDetailDto (IdentityMapper.cs:14-21), so refetching to
 * find out what happened would discard a response already in hand (section 3.2 rule D). There is no
 * detail key to seed, so the seed is a prefix patch.
 *
 * A. setQueriesData ON THE PREFIX, not setQueryData on one key. The user may have visited pages 1, 2
 *    and 3; writing only the key currently mounted leaves the other two holding the old row, which
 *    reappears the moment they page back.
 * B. THE PAGE IS TYPED PaginatedResponse<AccountantDetail>, NOT THE UNION. Only an AccountantAdmin can
 *    call these four (IdentityActionCatalogue.cs:26-29), so only Admin pages are ever patched.
 * C. THE INVALIDATION IS accountantKeys.lists AND NOT THE WHOLE CACHE (section 3.2 rule C). It is
 *    needed on top of the patch because suspend and demote run RequireAnActiveAdminRemainsAsync AFTER
 *    SaveChangesAsync and can be rolled back, and because a row's position in the displayName ordering
 *    is the server's to decide.
 */
function patchAccountantPages(queryClient: QueryClient, updated: AccountantDetail): void {
  queryClient.setQueriesData<PaginatedResponse<AccountantDetail>>(
    { queryKey: accountantKeys.lists },
    (page) =>
      page && {
        ...page,
        items: page.items.map((row) => (row.id === updated.id ? updated : row)),
      },
  );

  void queryClient.invalidateQueries({ queryKey: accountantKeys.lists });
}

/**
 * POST /api/accountants/invite. INVALIDATES ONLY; IT NEVER SPLICES THE NEW ROW IN.
 *
 * ListAccountantsHandler.cs:69-70 orders by DisplayName then Id, so a new Accountant may belong on a
 * page the operator is not looking at; inserting locally also leaves items.length inconsistent with
 * totalCount, which makes the pager wrong. The 201 body is still useful -- the screen names the
 * address in its Snackbar from what it submitted, not from the response.
 *
 * retry: false is inherited from queryClient.ts (section 3.4) and matters here specifically: nothing
 * in this API is idempotent and there is no idempotency key, so a retried invite is a spurious 409.
 */
export function useInviteAccountant() {
  const queryClient = useQueryClient();

  return useMutation<AccountantDetail, Error, InviteAccountantRequest>({
    mutationFn: inviteAccountant,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: accountantKeys.lists });
    },
  });
}

/**
 * The four row actions. NO OPTIMISTIC UPDATES ON ANY OF THEM (section 3.2 rule E), and here for a
 * specific reason rather than as a general policy: suspend and demote call
 * RequireAnActiveAdminRemainsAsync AFTER SaveChangesAsync, inside the transaction
 * (SuspendAccountantHandler.cs:70, DemoteAccountantHandler.cs:59, AccountInvariants.cs:30-47), so a
 * refused write is rolled back after having appeared to succeed. An optimistic row would suspend,
 * unsuspend, and only then show the 422.
 */
export function useSuspendAccountant() {
  const queryClient = useQueryClient();

  return useMutation<AccountantDetail, Error, string>({
    mutationFn: suspendAccountant,
    onSuccess: (updated) => {
      patchAccountantPages(queryClient, updated);
    },
  });
}

export function useReactivateAccountant() {
  const queryClient = useQueryClient();

  return useMutation<AccountantDetail, Error, string>({
    mutationFn: reactivateAccountant,
    onSuccess: (updated) => {
      patchAccountantPages(queryClient, updated);
    },
  });
}

export function usePromoteAccountant() {
  const queryClient = useQueryClient();

  return useMutation<AccountantDetail, Error, string>({
    mutationFn: promoteAccountant,
    onSuccess: (updated) => {
      patchAccountantPages(queryClient, updated);
    },
  });
}

export function useDemoteAccountant() {
  const queryClient = useQueryClient();

  return useMutation<AccountantDetail, Error, string>({
    mutationFn: demoteAccountant,
    onSuccess: (updated) => {
      patchAccountantPages(queryClient, updated);
    },
  });
}
