import { post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type {
  ChangeEmployeeLoginEmailRequest,
  DepartEmployeeRequest,
  EmployeeDetail,
  EmployeeSelf,
  EmployeeSummary,
  InviteEmployeeRequest,
  ListEmployeesRequest,
  MarkedResult,
  OnboardCustomerRequest,
  OnboardCustomerResponse,
  RegisterEmployeeRequest,
  SetEmployeeRoleRequest,
  UpdateEmployeeRequest,
  UpdateOwnContactRequest,
} from './types';

/**
 * THE TYPED HTTP CLIENT for the Employees slice: one exported function per registered endpoint,
 * named for the endpoint, in the order EmployeesEndpoints.cs registers them. No React, no hooks, no
 * TanStack Query, so this file diffs line by line against that one file (GeneralUIArchitecture.md
 * section 2.5). Read the wrappers off the `MapPost` calls, never off the screens that consume them
 * -- that is how two endpoints were missed once already.
 *
 * THIRTEEN `post` CALLS AND NO `get`. Twelve `/api/employees` routes plus `onboardCustomer`.
 *
 * A. DO NOT "CORRECT" /list OR /get TO GET. Both are POST reads -- GeneralUIArchitecture.md
 *    section 2.3 rule C names them among exactly five in this application, because the filter object
 *    is too large for a query string. The `list` suffix predicts nothing about the verb, and a
 *    "corrected" GET matches no route and returns 405 with nothing in the body to explain it.
 * B. NO ID IN A PATH, ANYWHERE. No route parameter exists in this API: the SPA route carries the id
 *    and the request body carries it too (section 2.3 rule D). That is why there is no
 *    route-vs-body ambiguity to get wrong.
 * C. NO `fetch` HERE. shared/api/http.ts is the only module in the application allowed one
 *    (section 2.1), and it is the module that attaches `credentials: 'same-origin'`, parses
 *    ProblemDetails and fires the global 401 handler.
 * D. NO BASE URL AND NO `import.meta.env`. Every path is a relative string starting `/api/`: one
 *    origin, no CORS, one build artefact that must be correct everywhere (04-Infrastructure.md
 *    sections 1-3).
 * E. NO CACHING, NO INVALIDATION, NO ERROR HANDLING, NO RETRY. A non-2xx throws `ApiError` from
 *    http.ts and callers never see `{ data, error }` (section 2.3 rule E). Nothing here is
 *    idempotent and there is no idempotency key, so a retried `register` creates a second Employee.
 */

// ---------------------------------------------------------------------------------------------
// Reads
// ---------------------------------------------------------------------------------------------

/**
 * POST read: EmployeesEndpoints.cs:36. Returns a page of `EmployeeSummary`, ordered server-side by
 * family name, given name, id -- to match `idx_employees_customer_name`. There is no sort parameter,
 * so offer no column sorting.
 *
 * Send the WHOLE body every call: the request DTO is a required parameter, so an absent body is a
 * 400 about a missing request body rather than a 200 with the DTO's defaults.
 */
export const listEmployees = (
  body: ListEmployeesRequest,
): Promise<PaginatedResponse<EmployeeSummary>> => post('/api/employees/list', body);

/**
 * POST read: EmployeesEndpoints.cs:50. TWO RESPONSE SHAPES from one route --
 * `EmployeeDetailDto` for AA/AU/CA and `EmployeeSelfDto` for an `Employee`, which is why
 * `GetEmployeeHandler.Handle` is declared `Task<object>`. `.Produces<EmployeeDetailDto>()` declares
 * only one of the two (BACKEND_CHANGES_REQUIRED item 6).
 *
 * Callers pick the type from the SESSION ROLE, in queries.ts, before the call is made -- never by
 * sniffing a field off the response (EmployeesScreens.md section 2.3 rule A).
 *
 * A colleague's id is a 404, by design: the handler applies `WhereInCustomerScope` and then a second
 * `UserAccountId == accountId` filter for the `Employee` role, so a tax number cannot be read by
 * guessing an id (GetEmployeeHandler.cs:65). Render "Not found", never "forbidden".
 */
export const getEmployee = (employeeId: string): Promise<EmployeeDetail | EmployeeSelf> =>
  post('/api/employees/get', { employeeId });

// ---------------------------------------------------------------------------------------------
// Writes
// ---------------------------------------------------------------------------------------------

/**
 * EmployeesEndpoints.cs:21. Creates an ACCOUNTLESS Employee: no login is created and no email is
 * sent. Registering and inviting are two separate operations, two permissions and two audit
 * meanings; there is no transaction spanning them, so never chain the two POSTs behind one
 * checkbox.
 *
 * Never retry a failure: a retry creates a second Employee.
 */
export const registerEmployee = (body: RegisterEmployeeRequest): Promise<EmployeeDetail> =>
  post('/api/employees/register', body);

/**
 * EmployeesEndpoints.cs:69. A FULL REPLACEMENT of every field including the nullable ones --
 * "omitting WorkEmail clears it." Pre-fill from the loaded detail and submit all of it; a form that
 * sends only what was touched erases the rest with a 200 and no undo.
 *
 * Changing the work email does NOT change the address this person signs in with
 * (EmployeesEndpoints.cs:76-79). That is /api/employees/change-login-email, below.
 */
export const updateEmployee = (body: UpdateEmployeeRequest): Promise<EmployeeDetail> =>
  post('/api/employees/update', body);

/**
 * EmployeesEndpoints.cs:86. NO ID PARAMETER, DELIBERATELY -- EmployeesScreens.md section 7.5 rule A
 * and EmployeeWriteDtos.cs:46-60: "an EmployeeId here, however carefully checked, turns every future
 * edit of the handler into an opportunity to check it wrongly." The signature is the control: no
 * call site can pass what does not exist. A Customer Admin editing a COLLEAGUE uses
 * `updateEmployee` from /employees/:employeeId, which is scoped and audited differently.
 *
 * Also a full replacement of its two fields: `{ workEmail: null, contactPhone: null }` erases both,
 * with a 200. That is why /profile currently offers no submit button at all
 * (BACKEND_CHANGES_REQUIRED item 12).
 */
export const updateOwnContact = (body: UpdateOwnContactRequest): Promise<EmployeeSelf> =>
  post('/api/employees/update-own-contact', body);

/**
 * EmployeesEndpoints.cs:103. Creates the account and mails the invitation immediately. There is NO
 * un-invite and no delete-account endpoint: once invited, the only lever is `suspendEmployeeAccount`.
 *
 * THE RAW TOKEN NEVER REACHES THE BROWSER (EmployeesEndpoints.cs:111-112: "The token is never
 * returned in the response -- it goes to the invitee's mailbox and nowhere else."). There is nothing
 * to display, log or put in a URL. If a token ever appears here, stop and flag it.
 */
export const inviteEmployee = (body: InviteEmployeeRequest): Promise<EmployeeDetail> =>
  post('/api/employees/invite', body);

/**
 * EmployeesEndpoints.cs:119. `role` is an INTEGER; `MarkedResult` carries no state, so the caller
 * must invalidate rather than seed.
 *
 * NOT IMMEDIATE. Claims are minted at login, so the target's live session keeps the old role for up
 * to eight hours -- a demoted Customer Admin keeps administrative powers until the cookie expires.
 * The dialog says so; do not try to fix it with a poll.
 */
export const setEmployeeRole = (body: SetEmployeeRoleRequest): Promise<MarkedResult> =>
  post('/api/employees/set-role', body);

/**
 * EmployeesEndpoints.cs:135. Records the departure AND suspends the account, in one transaction.
 * Reversible only as a CORRECTION, via `reinstateEmployee`. The end date may be in the future, and
 * the record flips to Departed on submit regardless -- nothing here is scheduled.
 */
export const departEmployee = (body: DepartEmployeeRequest): Promise<MarkedResult> =>
  post('/api/employees/depart', body);

/**
 * EmployeesEndpoints.cs:151. Clears the end date and the departure timestamp and REACTIVATES THE
 * ACCOUNT in the same step (ReinstateEmployeeHandler.cs:97-98), so `reactivateEmployeeAccount` is
 * not a second step afterwards.
 *
 * A CORRECTION, NOT A RE-HIRE. Somebody who genuinely left and came back is registered again as a
 * new record; the server cannot tell the two apart and the audit entry only records which one the
 * caller chose, so the dialog copy is the whole control.
 */
export const reinstateEmployee = (employeeId: string): Promise<MarkedResult> =>
  post('/api/employees/reinstate', { employeeId });

/**
 * EmployeesEndpoints.cs:167. Moves the address the Employee SIGNS IN WITH. Leaves the work email,
 * the password and any live session alone (EmployeesEndpoints.cs:174-177), and touches no row on the
 * Employee itself (ChangeEmployeeLoginEmailHandler.cs:101-104).
 *
 * ACCOUNTANT ROLES ONLY, and nobody may change their own -- not even an AccountantAdmin. There is no
 * self-service endpoint and the Accountant-only one is not a precedent for adding one
 * (BACKEND_CHANGES_REQUIRED item 10, amended 2026-09-02).
 */
export const changeEmployeeLoginEmail = (
  body: ChangeEmployeeLoginEmailRequest,
): Promise<MarkedResult> => post('/api/employees/change-login-email', body);

/**
 * EmployeesEndpoints.cs:184. Revokes access WITHOUT ending employment -- an Active Employee with a
 * Suspended account is a normal, expected state. Reversible with `reactivateEmployeeAccount`.
 */
export const suspendEmployeeAccount = (employeeId: string): Promise<MarkedResult> =>
  post('/api/employees/suspend-account', { employeeId });

/**
 * EmployeesEndpoints.cs:198. Restores access. It does NOT reset a password and does NOT clear a
 * lockout (EmployeesEndpoints.cs:206-207), so the success copy must not promise that the person can
 * sign in. Refused for a departed Employee -- use `reinstateEmployee` there instead.
 */
export const reactivateEmployeeAccount = (employeeId: string): Promise<MarkedResult> =>
  post('/api/employees/reactivate-account', { employeeId });

// ---------------------------------------------------------------------------------------------
// The cross-slice seam
// ---------------------------------------------------------------------------------------------

/**
 * POST /api/customers/onboard -- EmployeesEndpoints.cs:227, registered by `MapOnboardingRoute` in
 * THIS slice's endpoint file, in another slice's namespace, deliberately and LOCKED
 * (EmployeesEndpoints.cs:214-224). This slice owns two of the operation's three steps -- the first
 * Employee and their invitation -- and therefore owns the transaction that makes all three atomic.
 * Giving `Customers` the edges it would need to `Identity` and to this slice's tables would be a
 * dependency cycle (03-SliceInventory.md section 1), and splitting it in two would let a Customer
 * exist with nobody able to log into it. Do not "tidy" it in either direction.
 *
 * THE WRAPPER LIVES HERE; THE SCREEN DOES NOT. `api.ts` mirrors the endpoint file, so the function
 * belongs to this slice; the form is `slices/customers/screens/OnboardCustomerScreen.tsx` on
 * `/customers/new`, and its mutation hook is in `slices/customers/queries.ts`. That slice imports
 * this function and `OnboardCustomerRequest`/`OnboardCustomerResponse` from `./types` --
 * GeneralUIArchitecture.md section 1.4 rule C permits another slice's `api.ts` and `types.ts` and
 * forbids its `queries.ts`. THE NAMES ARE FIXED; renaming any of the three breaks that slice.
 *
 * The body is NESTED -- `{ customer, firstAdmin }`. Flattening it binds both objects to their
 * defaults and answers 422 "Legal name is required." for a form that plainly had one.
 *
 * Answers 200, not 201 (`Results.Ok`), and the response carries all three ids and NO TOKEN.
 * `firstAdmin` has no `role` field: the handler chooses `UserRole.CustomerAdmin` itself.
 */
export const onboardCustomer = (
  body: OnboardCustomerRequest,
): Promise<OnboardCustomerResponse> => post('/api/customers/onboard', body);
