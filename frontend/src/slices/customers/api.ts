import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type {
  Customer,
  CustomerSelf,
  CustomerSummary,
  ListCustomersRequest,
  SetCustomerStatusRequest,
  UpdateCustomerContactRequest,
  UpdateCustomerLegalRequest,
} from './types';

/**
 * One thin typed wrapper per endpoint of Slices/Customers/CustomersEndpoints.cs. NO React, NO hooks,
 * NO TanStack Query -- queries.ts owns all of that (GeneralUIArchitecture.md section 2.5).
 *
 * SEVEN functions. The endpoint table of CustomersScreens.md section 1 has nine rows; `create` is
 * deliberately unwrapped (rule A) and `onboard` belongs to slices/employees/api.ts because
 * EmployeesEndpoints.cs:227 registers it (rule B).
 *
 * A. NO createCustomer, AND NOT BECAUSE IT IS UNIMPLEMENTED. POST /api/customers/create
 *    (CustomersEndpoints.cs:15-28) works, and returns 201 with a Location header -- the only endpoint
 *    in this slice that does. It is unwrapped because a Customer with no Employee and no user account
 *    has nobody who can log in to it, which 02-AuthorizationMatrix.md section 3 and
 *    CustomersScreens.md section 5.1 both call a dead record an Accountant Admin then has to
 *    remember to finish by hand. /customers/new posts to /api/customers/onboard, which creates the
 *    Customer, its first Employee and that Employee's CustomerAdmin account in ONE transaction
 *    (OnboardCustomerHandler.cs:70-151). Adding a createCustomer wrapper here is how a second, wrong
 *    "Add Customer" button gets written later.
 *
 * B. THE ONBOARDING WRAPPER IS NOT IN THIS FILE EITHER. /api/customers/onboard is a /api/customers/*
 *    path served by the Employees slice, on purpose (03-SliceInventory.md section 1: the route is
 *    named for the resource the user thinks about, the code lives with the slice that owns the
 *    write). api.ts wraps the endpoints CustomersEndpoints.cs registers; onboardCustomer lives in
 *    slices/employees/api.ts. OnboardCustomerScreen imports it across the slice boundary
 *    (GeneralUIArchitecture.md section 1.4 rule C) rather than this file re-wrapping it, so there is
 *    exactly one wrapper for that route in the SPA.
 *
 * C. TWO VERBS, AND THE ODD ONE IS `list`. /list is a POST whose body carries the filters AND the
 *    paging (CustomersEndpoints.cs:30-39) -- section 2.3 rule C, so a long search string can never
 *    hit a URL length limit and never lands in a proxy access log. /detail and /own are GETs.
 *    Do not "make list a GET for cacheability": TanStack Query caches on the key, not on the verb.
 *
 * D. NO try/catch ANYWHERE IN THIS FILE. http.ts throws ApiError on every non-2xx and the components
 *    render it through ErrorBanner (section 7.2). A catch here that returns null turns 403, 404, 422
 *    and 500 into one indistinguishable empty screen.
 *
 * E. NO fetch. http.ts is the only file in the SPA that calls it (section 2.2).
 */

/**
 * POST /api/customers/list (CustomersEndpoints.cs:30-39). Roles AccountantAdmin, AccountantUser --
 * CustomersActionCatalogue.cs:16 -- so a CustomerAdmin calling it gets 403, not an empty page.
 *
 * Send ALL FOUR keys every call. `status: null` and `search: null` mean "no filter"; NEVER '' -- see
 * types.ts, ListCustomersHandler.cs:31-33 compares the trimmed string case-sensitively against
 * "Active"/"Suspended" and 422s on anything else, so '' is an error rather than a synonym for "both".
 *
 * The server CLAMPS pageSize to 50 and floors pageNumber at 1 (PaginationDefaults, section 2.4 rule
 * B); it does not reject either. An over-run pageNumber is a 200 with `items: []`, which is why
 * usePaginatedQuery exposes isOverrunPage instead of the caller inventing an error.
 */
export function listCustomers(
  request: ListCustomersRequest,
): Promise<PaginatedResponse<CustomerSummary>> {
  return post<PaginatedResponse<CustomerSummary>>('/api/customers/list', request);
}

/**
 * GET /api/customers/detail?customerId=... (CustomersEndpoints.cs:41-51). A GET with the id as a
 * QUERY PARAMETER, not in a path segment and not in a body: the C# signature binds
 * `[AsParameters]`-style `Guid customerId` from the query string (:42). /api/customers/detail/{id}
 * is a 404 from the router.
 *
 * ROLES AccountantAdmin, AccountantUser, CustomerAdmin (CustomersActionCatalogue.cs:19). A
 * CustomerAdmin asking for a customerId that is not their own gets 404, NOT 403 --
 * GetCustomerHandler.cs applies CustomerScope.WhereMatchesCustomerScope before the null check, and
 * that filter tests the PRIMARY KEY here because the Customer IS the tenant boundary. Render
 * NotFoundPage for it (section 2.3 rule J); "you do not have permission" would confirm the row
 * exists.
 */
export function getCustomer(customerId: string): Promise<Customer> {
  const query = new URLSearchParams({ customerId });
  return get<Customer>(`/api/customers/detail?${query.toString()}`);
}

/**
 * GET /api/customers/own (CustomersEndpoints.cs:53-61). NO PARAMETER AT ALL, deliberately: the
 * Customer is resolved from the caller's own session claim (GetOwnCustomerHandler.cs:22-33), so
 * there is nothing a caller could tamper with to read someone else's.
 *
 * Returns CustomerSelf -- ELEVEN keys, five fewer than Customer. The five missing ones are absent
 * from the RESPONSE, not hidden by the UI (02-AuthorizationMatrix.md:311).
 *
 * ROLES CustomerAdmin, Employee (CustomersActionCatalogue.cs:22) -- an Accountant calling this gets
 * 403, which is why /my-customer is not in an Accountant's navigation.
 */
export function getOwnCustomer(): Promise<CustomerSelf> {
  return get<CustomerSelf>('/api/customers/own');
}

/**
 * POST /api/customers/update-contact (CustomersEndpoints.cs:63-73). Returns the FULL updated
 * Customer (`.Produces<CustomerDto>()`), which queries.ts seeds straight into the detail cache
 * instead of refetching.
 *
 * A FULL REPLACEMENT: send all eight keys, including unchanged ones
 * (UpdateCustomerContactHandler.cs:47-53 assigns every field). Optional fields absent from the form
 * go as null, never ''.
 *
 * ROLES AccountantAdmin, AccountantUser, CustomerAdmin (CustomersActionCatalogue.cs:20) -- the only
 * write in this slice a Customer-side user may perform, and the reason
 * EditCustomerContactDialog serves both /customers/:customerId and /my-customer.
 *
 * NO 409: contact details are not unique. This endpoint does not declare one
 * (CustomersEndpoints.cs:70-72).
 */
export function updateCustomerContact(request: UpdateCustomerContactRequest): Promise<Customer> {
  return post<Customer>('/api/customers/update-contact', request);
}

/**
 * POST /api/customers/update-legal (CustomersEndpoints.cs:75-86). Returns the full updated Customer.
 *
 * THE ONLY ENDPOINT IN THIS SLICE THAT CAN RETURN 409 (:84, and it is the only one that declares
 * one): taxNumber is unique across customers. UpdateCustomerLegalHandler.cs raises it twice -- a
 * pre-check at :48-50 and a unique-violation catch on SQLSTATE 23505 at :62-65 for the concurrent
 * case -- with the SAME title both times, "A customer with this tax number already exists." One
 * message, two paths, so the dialog needs no special case for the race.
 *
 * ROLES AccountantAdmin, AccountantUser only (CustomersActionCatalogue.cs:19). A CustomerAdmin may
 * NOT rename their own company or change its tax number; that is the split between the two edit
 * dialogs.
 */
export function updateCustomerLegal(request: UpdateCustomerLegalRequest): Promise<Customer> {
  return post<Customer>('/api/customers/update-legal', request);
}

/**
 * POST /api/customers/suspend (CustomersEndpoints.cs:88-98). Returns the full updated Customer with
 * status "Suspended".
 *
 * IDEMPOTENT? NO -- suspending an already-suspended Customer is
 * 422 "This customer is already suspended." (SuspendCustomerHandler.cs:49-50), rendered verbatim in
 * the dialog, which stays open.
 *
 * `reason` is OPTIONAL and goes ONLY into the audit entry's After payload (:56-67). It is not
 * returned, not stored on the row and not rendered anywhere -- so the dialog must not require it and
 * must not promise it will be displayed later.
 *
 * ROLE AccountantAdmin ONLY (CustomersActionCatalogue.cs:14).
 */
export function suspendCustomer(request: SetCustomerStatusRequest): Promise<Customer> {
  return post<Customer>('/api/customers/suspend', request);
}

/**
 * POST /api/customers/reactivate (CustomersEndpoints.cs:100-110). The exact mirror of suspend, down
 * to sharing SetCustomerStatusRequestDto: 422 "This customer is already active."
 * (ReactivateCustomerHandler.cs:47-48), optional audit-only reason, AccountantAdmin only
 * (CustomersActionCatalogue.cs:15).
 */
export function reactivateCustomer(request: SetCustomerStatusRequest): Promise<Customer> {
  return post<Customer>('/api/customers/reactivate', request);
}
