import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { UserRole, type EmployeeStatus } from '../../shared/format/enums';
import { useSession } from '../../shared/auth/useSession';
import {
  usePaginatedQuery,
  type UsePaginatedQueryResult,
} from '../../shared/hooks/usePaginatedQuery';
import {
  changeEmployeeLoginEmail,
  departEmployee,
  getEmployee,
  inviteEmployee,
  listEmployees,
  reactivateEmployeeAccount,
  registerEmployee,
  reinstateEmployee,
  setEmployeeRole,
  suspendEmployeeAccount,
  updateEmployee,
  updateOwnContact,
} from './api';
import type {
  ChangeEmployeeLoginEmailRequest,
  DepartEmployeeRequest,
  EmployeeDetail,
  EmployeeSelf,
  EmployeeSummary,
  InviteEmployeeRequest,
  MarkedResult,
  RegisterEmployeeRequest,
  SetEmployeeRoleRequest,
  UpdateEmployeeRequest,
  UpdateOwnContactRequest,
} from './types';

/**
 * THE HOOKS. Screens import from this file and NEVER from `api.ts` (GeneralUIArchitecture.md
 * section 3.2 rule A): a screen calling the client directly has no cache key, so nothing else on the
 * page learns that anything changed.
 *
 * A. THE SHAPE OF /api/employees/get IS DECIDED BY THE SESSION ROLE, BEFORE THE CALL. Two hooks, two
 *    return types, two cache keys (EmployeesScreens.md section 2.3). Never `'status' in response`,
 *    never `response.status !== undefined`, never a Zod union discriminated on optionality. All three
 *    work today and all three break SILENTLY the first time a field moves, by sending a full record
 *    down the narrow branch. `status` is the worst possible sniffing key: it collides with
 *    `ApiError.status`, with the HTTP status and with `accountStatus`, so the bug reads as correct
 *    code. One key holding either shape is the same defect: a role change then hands a component the
 *    other type.
 *
 * B. THE SIX `MarkedResult` MUTATIONS CANNOT SEED THE CACHE. `{ success: true }` carries no state, so
 *    invalidating the detail and the list is the only way a screen learns the new state, and it is a
 *    real second round trip. Do not paper over it with a guessed `accountStatus`: section 3.2 rule E
 *    bans optimistic updates outright, and the client could not guess correctly anyway --
 *    `suspend-account` changes a value the LIST DTO never returns.
 *
 * C. `enabled` EXPRESSES A DATA DEPENDENCY, NEVER A PERMISSION. `useEmployeeDetail` is disabled for
 *    the `Employee` role because that role receives a DIFFERENT TYPE, not because it is forbidden;
 *    the list is disabled for an Accountant who has picked no Customer because the screen spec renders
 *    an EmptyState there instead (section 4.3 callout). Page-level permission is `RequireRole` in the
 *    route table and `can()` on the affordance (section 3.2 rule B).
 *
 * D. NOTHING HERE POLLS. No `refetchInterval`, including to observe the eight-hour role-change lag:
 *    the notification unread count is the only polling query in the application (section 3.2 rule H).
 */

/**
 * The key factory. Every filter appears in the list key -- omit `searchTerm` and two searches share
 * one cache entry, so the table shows the previous query's rows under the new query's pager
 * (EmployeesScreens.md section 4.2 rule A).
 */
export const employeeKeys = {
  all: ['employees'] as const,
  lists: ['employees', 'list'] as const,
  list: (filters: EmployeeListFilters) => ['employees', 'list', filters] as const,
  /** `EmployeeDetail` -- AA, AU, CA. */
  detail: (employeeId: string) => ['employees', 'detail', employeeId] as const,
  /** `EmployeeSelf` -- the `Employee` role, and the response of update-own-contact. */
  self: (employeeId: string) => ['employees', 'self', employeeId] as const,
};

/**
 * Every value that changes what the list returns. `status: null` and `hasAccount: null` mean "both",
 * which is the correct default -- the endpoint returns Active AND Departed unless filtered, and a
 * client-side default of "Active" makes a Customer Admin think a record is gone when nothing ever
 * deletes an Employee.
 */
export interface EmployeeListFilters {
  /** Accountant roles only. A CustomerAdmin naming another Customer is a 403, so the control is hidden. */
  customerId: string | null;
  status: EmployeeStatus | null;
  hasAccount: boolean | null;
  /** Already debounced and capped at 200 characters by the screen. `null`, never `''`. */
  searchTerm: string | null;
  pageNumber: number;
  pageSize: number;
}

// ---------------------------------------------------------------------------------------------
// Reads
// ---------------------------------------------------------------------------------------------

/**
 * The list. Built on `usePaginatedQuery` and nothing else (section 3.2 rule G), which clamps
 * `pageSize` to `MAX_PAGE_SIZE` on the way out and exposes `isOverrunPage` for the
 * `items: [] with totalCount > 0` case.
 *
 * RENDER THE PAGER FROM THE RESPONSE, never from the value sent: the server CLAMPS `pageSize` to 50
 * rather than rejecting it, so a request for 999 answers 200 with 50 rows and a pager built from the
 * request would be wrong by a factor of twenty.
 */
export function useEmployeeList(
  filters: EmployeeListFilters,
  options?: { enabled?: boolean },
): UsePaginatedQueryResult<EmployeeSummary> {
  return usePaginatedQuery<EmployeeSummary>({
    queryKey: employeeKeys.list(filters),
    queryFn: (page) =>
      listEmployees({
        customerId: filters.customerId,
        status: filters.status,
        hasAccount: filters.hasAccount,
        searchTerm: filters.searchTerm,
        pageNumber: page.pageNumber,
        pageSize: page.pageSize,
      }),
    pageNumber: filters.pageNumber,
    pageSize: filters.pageSize,
    ...(options?.enabled === undefined ? {} : { enabled: options.enabled }),
  });
}

/**
 * The WIDE shape, for AA / AU / CA. Disabled for the `Employee` role -- rule C: that role receives
 * `EmployeeSelf` from the same route, so running this hook would put a narrow record under the detail
 * key and every field access below would read `undefined`.
 *
 * The `as Promise<EmployeeDetail>` is the ONE place the two-shape endpoint is narrowed, and it is
 * narrowed by the role that was checked on the line above -- not by inspecting the response.
 */
export function useEmployeeDetail(employeeId: string) {
  const session = useSession();
  const role = session.status === 'authenticated' ? session.session.role : undefined;

  return useQuery<EmployeeDetail>({
    queryKey: employeeKeys.detail(employeeId),
    queryFn: () => getEmployee(employeeId) as Promise<EmployeeDetail>,
    enabled: employeeId !== '' && role !== undefined && role !== UserRole.Employee,
  });
}

/**
 * The NARROW shape, for the `Employee` role's own record. A colleague's id answers 404 from
 * GetEmployeeHandler.cs:70 by design -- render "Not found", never "forbidden"
 * (EmployeesScreens.md section 9 rule C).
 */
export function useOwnEmployeeRecord(employeeId: string) {
  const session = useSession();
  const role = session.status === 'authenticated' ? session.session.role : undefined;

  return useQuery<EmployeeSelf>({
    queryKey: employeeKeys.self(employeeId),
    queryFn: () => getEmployee(employeeId) as Promise<EmployeeSelf>,
    enabled: employeeId !== '' && role === UserRole.Employee,
  });
}

// ---------------------------------------------------------------------------------------------
// Writes that return a full record -- seed the detail key, invalidate the list
// ---------------------------------------------------------------------------------------------

/**
 * The response IS the new record, so seed the detail key with it (section 3.2 rule D) and invalidate
 * the list, whose ordering and page boundaries this may have changed.
 */
export function useRegisterEmployee() {
  const queryClient = useQueryClient();

  return useMutation<EmployeeDetail, Error, RegisterEmployeeRequest>({
    mutationFn: registerEmployee,
    onSuccess: (created) => {
      queryClient.setQueryData(employeeKeys.detail(created.id), created);
      void queryClient.invalidateQueries({ queryKey: employeeKeys.lists });
    },
  });
}

/** Same seeding rule. The response is the record after a FULL replacement of its fields. */
export function useUpdateEmployee() {
  const queryClient = useQueryClient();

  return useMutation<EmployeeDetail, Error, UpdateEmployeeRequest>({
    mutationFn: updateEmployee,
    onSuccess: (updated) => {
      queryClient.setQueryData(employeeKeys.detail(updated.id), updated);
      void queryClient.invalidateQueries({ queryKey: employeeKeys.lists });
    },
  });
}

/**
 * Invite returns the detail DTO with `hasAccount: true` and `accountStatus: "Invited"`, so it seeds
 * like the other two. The token is not in it and never will be.
 */
export function useInviteEmployee() {
  const queryClient = useQueryClient();

  return useMutation<EmployeeDetail, Error, InviteEmployeeRequest>({
    mutationFn: inviteEmployee,
    onSuccess: (invited) => {
      queryClient.setQueryData(employeeKeys.detail(invited.id), invited);
      void queryClient.invalidateQueries({ queryKey: employeeKeys.lists });
    },
  });
}

/**
 * Seeds the SELF key, not the detail key -- a different shape under a different key (rule A). This
 * response is also the only place a Customer-side caller ever learns their own employee id
 * (EmployeesScreens.md section 7.4), which is why the seed is worth doing even though no screen can
 * currently trigger it: /profile has no submit button while BACKEND_CHANGES_REQUIRED item 12 is open.
 *
 * The list is invalidated too, because a Customer Admin's own row appears in it.
 */
export function useUpdateOwnContact() {
  const queryClient = useQueryClient();

  return useMutation<EmployeeSelf, Error, UpdateOwnContactRequest>({
    mutationFn: updateOwnContact,
    onSuccess: (own) => {
      queryClient.setQueryData(employeeKeys.self(own.id), own);
      void queryClient.invalidateQueries({ queryKey: employeeKeys.lists });
    },
  });
}

// ---------------------------------------------------------------------------------------------
// Writes that return { success: true } -- invalidate both keys, seed nothing (rule B)
// ---------------------------------------------------------------------------------------------

/** Invalidates the detail and the list. Both are needed: the list shows `role`, the detail shows both statuses. */
export function useSetEmployeeRole() {
  const queryClient = useQueryClient();

  return useMutation<MarkedResult, Error, SetEmployeeRoleRequest>({
    mutationFn: setEmployeeRole,
    onSuccess: (_result, variables) => {
      invalidateEmployee(queryClient, variables.employeeId);
    },
  });
}

/** Records the departure and suspends the account, so BOTH chips on the detail screen change. */
export function useDepartEmployee() {
  const queryClient = useQueryClient();

  return useMutation<MarkedResult, Error, DepartEmployeeRequest>({
    mutationFn: departEmployee,
    onSuccess: (_result, variables) => {
      invalidateEmployee(queryClient, variables.employeeId);
    },
  });
}

/**
 * Reinstate clears the departure AND reactivates the account, and the account may come back as
 * `Invited` rather than `Active` -- that is somebody who was invited but never accepted before they
 * were departed. So the invalidated `Access:` chip is the truth, and no success copy may claim they
 * can sign in.
 */
export function useReinstateEmployee() {
  const queryClient = useQueryClient();

  return useMutation<MarkedResult, Error, string>({
    mutationFn: reinstateEmployee,
    onSuccess: (_result, employeeId) => {
      invalidateEmployee(queryClient, employeeId);
    },
  });
}

/**
 * INVALIDATE BOTH EVEN THOUGH NOTHING VISIBLE CHANGED. The work email did not move and
 * `EmployeeDetail` carries no login email, so a UI that only re-reads `workEmail` shows nothing
 * happening and the operator runs the operation again (EmployeesScreens.md section 8.7 rule F).
 */
export function useChangeLoginEmail() {
  const queryClient = useQueryClient();

  return useMutation<MarkedResult, Error, ChangeEmployeeLoginEmailRequest>({
    mutationFn: changeEmployeeLoginEmail,
    onSuccess: (_result, variables) => {
      invalidateEmployee(queryClient, variables.employeeId);
    },
  });
}

/** Revokes access without ending employment: `accountStatus` changes, `status` does not. */
export function useSuspendAccount() {
  const queryClient = useQueryClient();

  return useMutation<MarkedResult, Error, string>({
    mutationFn: suspendEmployeeAccount,
    onSuccess: (_result, employeeId) => {
      invalidateEmployee(queryClient, employeeId);
    },
  });
}

/** Restores access. Resets no password and clears no lockout -- see the success copy in the dialog's caller. */
export function useReactivateAccount() {
  const queryClient = useQueryClient();

  return useMutation<MarkedResult, Error, string>({
    mutationFn: reactivateEmployeeAccount,
    onSuccess: (_result, employeeId) => {
      invalidateEmployee(queryClient, employeeId);
    },
  });
}

/**
 * The one invalidation the six stateless writes share. Kept as a function rather than repeated so a
 * future write cannot invalidate one key and forget the other -- which reads as "the screen did not
 * update" and is diagnosed as a server bug.
 */
function invalidateEmployee(
  queryClient: ReturnType<typeof useQueryClient>,
  employeeId: string,
): void {
  void queryClient.invalidateQueries({ queryKey: employeeKeys.detail(employeeId) });
  void queryClient.invalidateQueries({ queryKey: employeeKeys.lists });
}
