import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import {
  usePaginatedQuery,
  type UsePaginatedQueryResult,
} from '../../shared/hooks/usePaginatedQuery';
import { getAuditActionCodes, getAuditEntry, searchAuditLog } from './api';
import { isGuid } from './guid';
import type {
  AuditActionCodes,
  AuditEntry,
  AuditEntryDetail,
  AuditSearchRequest,
} from './types';

/**
 * Keys are [sliceName, resource, ...discriminators] (GeneralUIArchitecture.md section 3.1),
 * exported as one object so no screen builds an array literal.
 *
 * NOTHING IN THE APP WRITES TO AN ['audit', ...] KEY, so `all` is not an invalidation prefix --
 * there are no mutations here and therefore no invalidations (AuditScreens.md section 3.3 rule B).
 * It exists so the prefix is written once.
 */
export const auditKeys = {
  all: ['audit'] as const,
  /**
   * THE WHOLE REQUEST OBJECT, paging included. A key missing one filter -- or missing pageNumber --
   * lets two different searches share one cache entry, and the table then shows the wrong rows
   * under the right heading. On an audit tool that is invisible, because the rows still look
   * plausible (section 3.1, AuditScreens.md section 3.2 rule A).
   */
  search: (request: AuditSearchRequest) => ['audit', 'search', request] as const,
  detail: (auditEntryId: string) => ['audit', 'detail', auditEntryId] as const,
  actionCodes: ['audit', 'actionCodes'] as const,
};

/**
 * THROUGH usePaginatedQuery, NEVER useQuery (section 3.2 rule G), so the clamp trap is handled in
 * one place: PaginatedQuery.Normalize clamps pageSize to [1, 50] and substitutes 15 for anything
 * <= 0 (SearchAuditLogHandler.cs:35; punch-list item 17), so asking for 999 yields 50 with a 200.
 * The pager is rendered from response.pageSize by PaginatedTable, never from the value sent.
 *
 * IT KEEPS THE KERNEL'S 30-SECOND staleTime, deliberately opposite to useAuditActionCodes below.
 * The log grows continuously; a search cached forever tells an investigator the log stopped.
 *
 * `enabled` is a genuine data dependency here -- the filter set has to be valid before there is
 * anything to ask for -- and NEVER an expression of "not allowed" (section 3.2 rule B).
 */
export function useAuditSearch(
  request: AuditSearchRequest,
  options?: { enabled?: boolean },
): UsePaginatedQueryResult<AuditEntry> {
  return usePaginatedQuery<AuditEntry>({
    queryKey: auditKeys.search(request),
    queryFn: ({ pageNumber, pageSize }) => searchAuditLog({ ...request, pageNumber, pageSize }),
    pageNumber: request.pageNumber,
    pageSize: request.pageSize,
    ...(options?.enabled === undefined ? {} : { enabled: options.enabled }),
  });
}

/**
 * `enabled` IS FOR THE ID, NEVER FOR PERMISSION (section 3.2 rule B). A malformed auditEntryId is
 * a 400 from parameter binding (AuditEndpoints.cs:28) whose body says nothing actionable, so the
 * screen renders NotFoundPage and issues no request at all.
 *
 * Gating on permission would render an empty screen where a denial belongs; the route guard and
 * the server's ReadAuditLog grant do that job.
 */
export function useAuditEntry(auditEntryId: string): UseQueryResult<AuditEntryDetail, Error> {
  return useQuery<AuditEntryDetail, Error>({
    queryKey: auditKeys.detail(auditEntryId),
    queryFn: () => getAuditEntry(auditEntryId),
    enabled: isGuid(auditEntryId),
  });
}

/**
 * THE ONE QUERY IN THIS SLICE THAT DEPARTS FROM THE KERNEL'S 30-SECOND staleTime, and the two
 * policies sit a few lines apart on purpose.
 *
 * The catalogues are compile-time constants: ListAuditActionsHandler.cs:27-35 projects
 * AuditActions.All, AuditTargets.All and AuditOutcome.All, ordinal-sorted, with no DbContext
 * injected at all (:19-21). The response can only change on a deploy, and a deploy reloads the SPA.
 *
 * gcTime: Infinity keeps it for the session so an unmount does not re-request it -- one request per
 * session however many searches are run. No refetchOnMount: 'always', which is inert against
 * staleTime: Infinity until someone sets it and then costs one extra request per navigation to an
 * already query-heavy screen.
 */
export function useAuditActionCodes(): UseQueryResult<AuditActionCodes, Error> {
  return useQuery<AuditActionCodes, Error>({
    queryKey: auditKeys.actionCodes,
    queryFn: getAuditActionCodes,
    staleTime: Infinity,
    gcTime: Infinity,
  });
}
