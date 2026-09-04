/**
 * The one GUID test in this slice, needed in three places that must all agree:
 *
 *   1. AuditEntryScreen -- validate the path parameter BEFORE fetching. AuditEndpoints.cs:28 binds
 *      `Guid auditEntryId` from the query string, so a malformed value is a 400 from parameter
 *      binding whose body says nothing a reader can act on (AuditScreens.md section 4 rule A).
 *   2. queries.ts -- `enabled` for useAuditEntry, a genuine data dependency and never a permission.
 *   3. auditFilterSchema -- customerId is a Guid?, so a non-GUID is a binding 400, not a 422
 *      (SearchAuditLogRequestDto.cs:18).
 *
 * NOT A DATE, MONEY OR ENUM HELPER, so it is not one of the decisions shared/format owns; there is
 * no GUID helper under shared/ to reuse and this plan creates no file there.
 *
 * The "D" format only -- 8-4-4-4-12 hex digits with hyphens. .NET's Guid.TryParse also accepts
 * "N", "B" and "P" ("{...}", "(...)", no hyphens), which no link this SPA builds ever produces:
 * System.Text.Json writes "D" and every id in the UI arrives from a JSON response. A stricter
 * client than the server is only a problem if a legitimate value is refused, and none is.
 */
const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function isGuid(value: string | undefined): boolean {
  return value !== undefined && GUID.test(value);
}
