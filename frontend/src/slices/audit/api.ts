import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type {
  AuditActionCodes,
  AuditEntry,
  AuditEntryDetail,
  AuditSearchRequest,
} from './types';

/**
 * One function per endpoint, named for the endpoint and not for the screen
 * (GeneralUIArchitecture.md section 2.5), verified line by line against
 * AccountantApp.Api/Slices/Audit/AuditEndpoints.cs:
 *
 *   searchAuditLog      POST /api/audit/search        :20   PaginatedResponse<AuditEntryDto>, 403, 422
 *   getAuditEntry       GET  /api/audit/detail        :28   AuditEntryDetailDto, 403, 404
 *   getAuditActionCodes GET  /api/audit/action-codes  :39   AuditActionsResponseDto, 403
 *
 * THREE ROUTES AND ALL THREE READ. AuditEndpoints.cs maps nothing else and IAuditApi.cs exposes
 * only WRITE members to other slices, so there is no fourth function to add and no query member to
 * wrap. The audit log is append-only: 20260901_002_ReshapeAuditEntries.sql ends with
 * "Append-only. No UPDATE or DELETE path exists in the application."
 *
 * NO ROUTE DECLARES 401 AND ALL THREE CAN RETURN IT (punch-list item 32): authentication throws in
 * the CurrentUser factory, before the handler runs. shared/api/http.ts handles it centrally.
 *
 * NO REACT, NO HOOKS, NO TANSTACK QUERY HERE (section 2.5). Cache policy is queries.ts's job.
 */

/**
 * A POST THAT READS, AND THAT IS CORRECT (AuditEndpoints.cs:18-19; section 2.3 rule C names it as
 * one of the API's deliberate POST reads). Do not "fix" it to a GET: that is a 405 with nothing in
 * the body to explain it.
 *
 * THE FILTERS GO IN THE BODY. The route binds SearchAuditLogRequestDto from the body (:20), so a
 * POST carrying them in the query string binds every filter to null and returns the WHOLE LOG with
 * a 200 and no complaint -- a search that silently ignores everything the user set.
 */
export const searchAuditLog = (
  body: AuditSearchRequest,
): Promise<PaginatedResponse<AuditEntry>> => post('/api/audit/search', body);

/**
 * The id is in the QUERY STRING, not the path and not a body (AuditEndpoints.cs:28). Built with
 * URLSearchParams rather than interpolated: a raw interpolation of a value containing `&` or `#`
 * produces a malformed query the server answers with a 400.
 */
export const getAuditEntry = (auditEntryId: string): Promise<AuditEntryDetail> =>
  get(`/api/audit/detail?${new URLSearchParams({ auditEntryId }).toString()}`);

/** No parameters. ListAuditActionsHandler injects no DbContext: everything it returns is a constant. */
export const getAuditActionCodes = (): Promise<AuditActionCodes> =>
  get('/api/audit/action-codes');
