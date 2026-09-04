import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import {
  usePaginatedQuery,
  type UsePaginatedQueryResult,
} from '../../shared/hooks/usePaginatedQuery';
import {
  getCustomer,
  getOwnCustomer,
  listCustomers,
  reactivateCustomer,
  suspendCustomer,
  updateCustomerContact,
  updateCustomerLegal,
} from './api';
import type {
  Customer,
  CustomerSelf,
  CustomerStatus,
  CustomerSummary,
  SetCustomerStatusRequest,
  UpdateCustomerContactRequest,
  UpdateCustomerLegalRequest,
} from './types';

/**
 * Seven hooks: three reads, four writes. Screens import from HERE and never from api.ts
 * (GeneralUIArchitecture.md section 3.2 rule A), so every caching decision in this slice is in this
 * one file instead of scattered through JSX.
 *
 * THE FOUR WRITES DO NOT ALL INVALIDATE THE SAME THING, and the differences are the point:
 *
 *   useUpdateCustomerContact   seeds detail, invalidates list, INVALIDATES own
 *   useUpdateCustomerLegal     seeds detail, invalidates list
 *   useSuspendCustomer         seeds detail, invalidates list
 *   useReactivateCustomer      seeds detail, invalidates list
 *
 * WHAT THIS FILE DOES NOT DO:
 *
 * - NO refetchInterval, anywhere. Section 3.2 rule H allows exactly one polling query in the whole
 *   application and it is the notification unread count. Not the list, not the detail, not `own`.
 * - NO can() CALL. queries.ts fetches; the screens decide what to draw. A hook that hides a fetch
 *   behind a permission check is section 3.2 rule B in disguise, and it shows an AccountantUser an
 *   empty table instead of the rows 02-AuthorizationMatrix.md section 2 entitles them to.
 * - NO onError THAT SWALLOWS A 403. Section 6.2 rule B: a can() of true followed by a 403 is a bug
 *   in Phase 0's permission table, and a catch here is how it stays one.
 * - NO OPTIMISTIC UPDATES. Section 3.2 rule E, and here the guess is concretely wrong: every string
 *   is trimmed and normalised server-side (CustomerValidation.cs:64-82), so a cache entry assembled
 *   from form values differs from the row that was written.
 * - NO retry ON MUTATIONS -- inherited from Phase 0's queryClient (section 3.4) and load-bearing
 *   here: no endpoint in this slice is idempotent, every write audits
 *   (UpdateCustomerContactHandler.cs:56-62), and a retried /suspend after a timeout is a second
 *   audited transition for one operator action.
 */

/**
 * Keys follow [sliceName, resource, ...discriminators] (section 3.1).
 *
 * `lists` is the blast radius of every write here: two segments, ['customers','list'], which
 * invalidates every cached page of every filter combination.
 *
 * ['customers','own'] TAKES NO DISCRIMINATOR, DELIBERATELY. An id in that key would imply a screen
 * that could show a different Customer, and /api/customers/own cannot -- it has no parameter
 * (CustomersScreens.md section 6.1).
 */
export const customerKeys = {
  all: ['customers'] as const,
  lists: ['customers', 'list'] as const,
  list: (filters: {
    status: CustomerStatus | null;
    search: string | null;
    pageNumber: number;
    pageSize: number;
  }) => ['customers', 'list', filters] as const,
  detail: (customerId: string) => ['customers', 'detail', customerId] as const,
  own: ['customers', 'own'] as const,
};

/**
 * POST /api/customers/list, for both Accountant roles.
 *
 * A. EVERY FILTER IS IN THE KEY. Omit `search` and two different searches share one cache entry, so
 *    the table shows the previous query's rows under the new query's pager (CustomersScreens.md
 *    section 3.2 rule A). Same for `status` and both page fields.
 * B. BUILT ON usePaginatedQuery AND NOTHING ELSE (section 3.2 rule G), so the pageSize clamp is
 *    handled in exactly one place in the SPA. The hook hands the clamped page back to queryFn, which
 *    is why the body is assembled inside it rather than from the caller's raw numbers.
 * C. NO `enabled`. Both Accountant roles may call this route (CustomersActionCatalogue.cs:16), and
 *    expressing a permission by disabling a query is forbidden (section 3.2 rule B).
 *
 * `status: null` and `search: null` mean "no filter" and are sent as nulls; NEVER '' -- see
 * types.ts and api.ts.
 */
export function useCustomerList(filters: {
  status: CustomerStatus | null;
  search: string | null;
  pageNumber: number;
  pageSize: number;
}): UsePaginatedQueryResult<CustomerSummary> {
  return usePaginatedQuery<CustomerSummary>({
    queryKey: customerKeys.list(filters),
    queryFn: (page) =>
      listCustomers({
        status: filters.status,
        search: filters.search,
        pageNumber: page.pageNumber,
        pageSize: page.pageSize,
      }),
    pageNumber: filters.pageNumber,
    pageSize: filters.pageSize,
  });
}

/**
 * GET /api/customers/detail?customerId=...
 *
 * THE ONE `enabled` IN THIS SLICE, and it expresses "the id is not known yet mid-navigation" and
 * nothing else (section 3.2 rule B). It is NEVER a permission check: if a screen should be
 * unreachable for a role, the route is gated with RequireRole (section 11.1).
 *
 * A 404 here is an out-of-scope OR non-existent Customer and the screen renders NotFoundPage for
 * both -- never "forbidden" (section 2.3 rule J). The server answers 404 on purpose, because a 403
 * would confirm the row exists.
 */
export function useCustomer(customerId: string | undefined): UseQueryResult<Customer, Error> {
  // Narrowed to a plain string once, so queryFn needs no assertion and cannot be called with
  // `undefined` concatenated into the query string (api.ts rule E).
  const id = customerId ?? '';

  return useQuery<Customer, Error>({
    queryKey: customerKeys.detail(id),
    queryFn: () => getCustomer(id),
    enabled: id !== '',
  });
}

/**
 * GET /api/customers/own, for CustomerAdmin and Employee.
 *
 * RETURNS CustomerSelf, THE NARROW SHAPE -- eleven keys. Nothing may write a wide `Customer` into
 * this key; see useUpdateCustomerContact.
 *
 * NO `enabled` AND NO 401 SPECIAL CASE. The reachable denial for an Accountant is a 403
 * (GetOwnCustomerHandler.cs:24 runs the permission check before reading CustomerId), both denials
 * are unreachable through the router, and section 2.3 rule H applies unchanged.
 */
export function useOwnCustomer(): UseQueryResult<CustomerSelf, Error> {
  return useQuery<CustomerSelf, Error>({
    queryKey: customerKeys.own,
    queryFn: getOwnCustomer,
  });
}

/**
 * POST /api/customers/update-contact. Roles AA, AU, CA.
 *
 * A. SEEDS THE DETAIL KEY. The endpoint returns the full CustomerDto
 *    (CustomersEndpoints.cs:70), so refetching would discard a response already in hand and open a
 *    window where the screen shows the old values after a successful save (section 3.2 rule D).
 * B. INVALIDATES ['customers','own'] AND NEVER SEEDS IT. update-contact returns the WIDE Customer;
 *    that key holds the NARROW CustomerSelf. Writing one into the other puts taxNumber, taxOffice,
 *    onboardedOn, createdAt and updatedAt into a cache entry typed CustomerSelf, and the next
 *    component that renders "everything in own" starts showing fields /my-customer is specified not
 *    to show -- from the cache, on a screen an Employee can open. Invalidating costs one extra GET
 *    on a rarely visited screen (CustomersScreens.md section 6.1).
 * C. ONE HOOK SERVES BOTH SCREENS. CustomerScope restricts the write to the caller's own row
 *    regardless (UpdateCustomerContactHandler.cs:39-43), so there is no second endpoint, no
 *    CA-specific hook and no second dialog. On /my-customer the first two cache writes are no-ops in
 *    that browser and cost nothing.
 */
export function useUpdateCustomerContact() {
  const queryClient = useQueryClient();

  return useMutation<Customer, Error, UpdateCustomerContactRequest>({
    mutationFn: updateCustomerContact,
    onSuccess: (updated) => {
      queryClient.setQueryData(customerKeys.detail(updated.id), updated);
      void queryClient.invalidateQueries({ queryKey: customerKeys.lists });
      void queryClient.invalidateQueries({ queryKey: customerKeys.own });
    },
  });
}

/**
 * POST /api/customers/update-legal. Roles AA and AU only -- a CustomerAdmin gets 403
 * (UpdateCustomerLegalHandler.cs:39), which is why this is a separate dialog from the contact one.
 *
 * Seeds detail, invalidates list. It does NOT touch ['customers','own']: only Accountants can call
 * it, and an Accountant cannot populate that key at all (/api/customers/own 403s for them).
 *
 * The 409 on a duplicate tax number is not handled here -- it reaches the dialog as an ApiError and
 * renders verbatim in a form banner. A catch here would turn it into a silent no-op.
 */
export function useUpdateCustomerLegal() {
  const queryClient = useQueryClient();

  return useMutation<Customer, Error, UpdateCustomerLegalRequest>({
    mutationFn: updateCustomerLegal,
    onSuccess: (updated) => {
      queryClient.setQueryData(customerKeys.detail(updated.id), updated);
      void queryClient.invalidateQueries({ queryKey: customerKeys.lists });
    },
  });
}

/**
 * POST /api/customers/suspend. AccountantAdmin only.
 *
 * Seeds detail with the returned row (status now "Suspended"), invalidates list so the chip in every
 * cached page corrects itself.
 *
 * IT DOES NOT INVALIDATE ['customers','own'], AND THAT IS NOT AN OVERSIGHT. Only an AccountantAdmin
 * can call this, and an Accountant cannot populate that key -- /api/customers/own is granted to
 * CustomerAdmin and Employee only (CustomersActionCatalogue.cs:22). Invalidating it would be a
 * guaranteed no-op that tells the next reader the author believed a cache existed which never does.
 *
 * THE onError IS PART OF THE SPECIFICATION, NOT DEFENSIVE CODE, AND IT SWALLOWS NOTHING.
 * 422 "This customer is already suspended." (SuspendCustomerHandler.cs:49-50) is reachable from a
 * stale tab whose chip still reads Active. CustomersScreens.md section 4.5 requires that the dialog
 * stay open with the message rendered verbatim AND that the detail query be invalidated so the chip
 * corrects itself. The invalidation is a cache decision, so it lives here rather than in the dialog;
 * the rejection still propagates to mutation.error, which is what the banner renders.
 */
export function useSuspendCustomer() {
  const queryClient = useQueryClient();

  return useMutation<Customer, Error, SetCustomerStatusRequest>({
    mutationFn: suspendCustomer,
    onSuccess: (updated) => {
      queryClient.setQueryData(customerKeys.detail(updated.id), updated);
      void queryClient.invalidateQueries({ queryKey: customerKeys.lists });
    },
    onError: (_error, variables) => {
      void queryClient.invalidateQueries({
        queryKey: customerKeys.detail(variables.customerId),
      });
    },
  });
}

/** POST /api/customers/reactivate. The exact mirror of useSuspendCustomer, same cache behaviour. */
export function useReactivateCustomer() {
  const queryClient = useQueryClient();

  return useMutation<Customer, Error, SetCustomerStatusRequest>({
    mutationFn: reactivateCustomer,
    onSuccess: (updated) => {
      queryClient.setQueryData(customerKeys.detail(updated.id), updated);
      void queryClient.invalidateQueries({ queryKey: customerKeys.lists });
    },
    onError: (_error, variables) => {
      void queryClient.invalidateQueries({
        queryKey: customerKeys.detail(variables.customerId),
      });
    },
  });
}
