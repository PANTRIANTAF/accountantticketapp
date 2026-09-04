import { get, post } from '../../shared/api/http';
import type { PaginatedResponse } from '../../shared/api/paginated';
import type {
  CreateTicketTypeRequest,
  EditTicketTypeRequest,
  TicketTypeDetail,
  TicketTypeListItem,
  ToggleTicketTypeRequest,
} from './types';

/**
 * Six functions, one per endpoint, named for the endpoint and not for the screen. Verified against
 * AccountantApp.Api/Slices/TicketTypes/TicketTypesEndpoints.cs, whose group is
 * MapGroup("/api/ticket-types") at :14:
 *
 *   createTicketType        POST /api/ticket-types/create    :16   201 + Location, 403, 409, 422
 *   editTicketType          POST /api/ticket-types/edit      :28   200, 403, 404, 422 -- and an
 *                                                                  UNDECLARED 409, see below
 *   toggleTicketType        POST /api/ticket-types/toggle     :37   200, 403, 404
 *   listTicketTypes         GET  /api/ticket-types/list       :45   200. All three params nullable
 *   getTicketType           GET  /api/ticket-types/detail     :56   200, 404. ticketTypeId REQUIRED
 *   getTicketTypeVersion    GET  /api/ticket-types/version    :63   200, 404. Both params REQUIRED
 *
 * THIS FILE CONTAINS NO REACT, NO HOOKS AND NO TANSTACK QUERY (GeneralUIArchitecture.md section
 * 2.5), so it can be read against the C# endpoint file line by line. Cache behaviour is queries.ts.
 *
 * THE THREE READS ARE GETs. GeneralUIArchitecture.md section 2.3 rule C lists the five POST reads in
 * this API and none of them is in this slice, so a POST to /list here is a 405. Do not "make it
 * consistent" with /api/employees/list.
 *
 * KEBAB-CASE, AT EVERY WORD BOUNDARY, WRITTEN ONCE. /api/tickettypes/list is a 404 that reads like a
 * missing endpoint and /api/ticketTypes/list is a 404 that reads like a casing bug in the server;
 * neither is caught by TypeScript, because both are string literals. The six paths appear in this
 * file and nowhere else in the slice.
 */

const BASE = '/api/ticket-types';

/**
 * ACTIVEONLY IS A THREE-STATE FILTER AND `false` DOES NOT MEAN "ALL".
 * ListTicketTypesHandler.cs:29-38 reads it as `query.Where(t => t.IsActive == req.ActiveOnly.Value)`
 * and only when HasValue, inside an `else if` whose `if` branch is the Customer-side one:
 *
 *   omitted -> active AND inactive     true -> active only     false -> INACTIVE ONLY
 *
 * So `undefined` must be OMITTED FROM THE QUERY STRING, never sent as `activeOnly=false`. Sending
 * false to mean "all" shows an Accountant nothing but deactivated types, which is indistinguishable
 * on screen from a catalogue that failed to load. Punch-list item 20; the caller's three-option
 * control is the mandatory workaround and a "simplification" to a checkbox reintroduces the bug.
 *
 * For a Customer-side caller the parameter is never read at all -- it sits in the `else` branch --
 * so the control is hidden for them rather than disabled (section 6.2 rule C).
 *
 * pageSize is CLAMPED to 50 and not rejected (PaginatedQuery.cs:10-12): ask for 200 and you get 50
 * with a 200 OK. The caller renders the pager from response.pageSize, never from what it sent.
 */
export function listTicketTypes(params: {
  pageNumber: number;
  pageSize: number;
  activeOnly?: boolean | undefined;
}): Promise<PaginatedResponse<TicketTypeListItem>> {
  // URLSearchParams, never template interpolation: it encodes, and nothing here has to remember to.
  const query = new URLSearchParams({
    pageNumber: String(params.pageNumber),
    pageSize: String(params.pageSize),
  });
  if (params.activeOnly !== undefined) query.set('activeOnly', String(params.activeOnly));

  return get<PaginatedResponse<TicketTypeListItem>>(`${BASE}/list?${query.toString()}`);
}

/**
 * ALWAYS THE CURRENT VERSION (GetTicketTypeHandler passes CurrentVersionOf), so versionNumber and
 * currentVersionNumber are always equal in this response.
 *
 * NEVER CALL THIS WITH AN EMPTY STRING. The query parameter is a NON-NULLABLE Guid
 * (TicketTypesEndpoints.cs:56), so a missing or unparseable value is a 400 from the model binder
 * before any handler runs -- routed through AppExceptionMiddleware.cs and shown to the user as a
 * sentence written by ASP.NET Core. Gate an unresolved route parameter with TanStack Query's
 * `enabled` (section 3.2 rule B), not with a try/catch.
 *
 * A 404 HERE IS THE DESIGNED ANSWER, NOT A FAULT. ApplyCustomerSideVisibility
 * (TicketTypeMapper.cs:33-37) applies the audience check AND IsActive, so a deactivated type is a
 * 404 for a Customer Admin or an Employee. Render "Not found" and never "forbidden" -- a 403 would
 * confirm the row exists. And never fall back from this 404 to getTicketTypeVersion "to get
 * something to show": that is precisely the discovery the 404 refused.
 */
export const getTicketType = (ticketTypeId: string): Promise<TicketTypeDetail> => {
  const query = new URLSearchParams({ ticketTypeId });
  return get<TicketTypeDetail>(`${BASE}/detail?${query.toString()}`);
};

/**
 * ONE VERSION BY NUMBER. Both parameters are non-nullable server-side
 * (TicketTypesEndpoints.cs:63) -- the same 400 hazard as getTicketType.
 *
 * THIS SUCCEEDS WHERE /detail RETURNS 404, FOR THE SAME TYPE AND THE SAME CALLER.
 * GetTicketTypeVersionHandler applies ApplyCustomerSideAudience -- audience only, no IsActive
 * (TicketTypeMapper.cs:39-43). That is correction note T-4, applied on purpose, so a Customer Admin
 * can still render the form of a ticket they raised last year after the type was retired. It is not
 * an inconsistency to normalise, and it is never a way to test whether a type exists.
 *
 * The range 1..currentVersionNumber is gapless -- EditTicketTypeHandler.cs:51 derives
 * `next = Max(VersionNumber) + 1` -- but a 404 is still rendered as "That version does not exist"
 * rather than crashing the stepper, because a future backfill migration is not something the client
 * can rule out.
 */
export const getTicketTypeVersion = (
  ticketTypeId: string,
  versionNumber: number,
): Promise<TicketTypeDetail> => {
  const query = new URLSearchParams({
    ticketTypeId,
    versionNumber: String(versionNumber),
  });
  return get<TicketTypeDetail>(`${BASE}/version?${query.toString()}`);
};

/**
 * Returns 201 with `Location: /api/ticket-types/detail?ticketTypeId=<id>`
 * (TicketTypesEndpoints.cs:20). http.ts returns the parsed body for any response.ok, so this
 * resolves with the full TicketTypeDetail.
 *
 * DO NOT FOLLOW THE LOCATION HEADER. It is a second round trip for data already in hand, and it
 * re-reads through ToDetail a version you were just given. Seed the cache from the body
 * (section 3.2 rule D).
 *
 * The 409 is a duplicate code, compared CASE-INSENSITIVELY: CreateTicketTypeHandler.cs:45 compares
 * `t.Code.ToLower() == req.Code.ToLower()`, and the unique index on LOWER(code)
 * (20260829_001_CreateTicketTypesSchema.sql:18) catches the race the pre-check cannot. Its message
 * is "A Ticket Type with this code already exists" -- note the ABSENT full stop
 * (CreateTicketTypeHandler.cs:17). Render it verbatim; do not add one and do not paraphrase.
 */
export const createTicketType = (body: CreateTicketTypeRequest): Promise<TicketTypeDetail> =>
  post<TicketTypeDetail>(`${BASE}/create`, body);

/**
 * `fields` is a FULL REPLACEMENT that mints a new version, every time, including a save that changed
 * nothing -- there is no no-op path here, unlike toggle.
 *
 * IT CAN RETURN A 409 THAT THE ENDPOINT DOES NOT DECLARE. TicketTypesEndpoints.cs:31-35 declares
 * 403, 404 and 422 only, while EditTicketTypeHandler.cs:70-73 catches
 * PostgresException { SqlState: "23505" } and throws 409 "This ticket type was edited by someone
 * else. Reload and try again." Harmless here -- this client branches on response.status, not on
 * declared metadata -- but a generated client (punch-list item 9) would not know the status exists.
 *
 * A 422 CAN PRECEDE A 404. EditTicketTypeHandler.cs:38-49 runs the permission check, then
 * normalisation, then all three validators, and only THEN loads the type and throws 404 if it is
 * missing. So a 422 is not proof that the type exists.
 */
export const editTicketType = (body: EditTicketTypeRequest): Promise<TicketTypeDetail> =>
  post<TicketTypeDetail>(`${BASE}/edit`, body);

/** Mints no version, writes no audit entry when the state already holds, and returns 200 either way. */
export const toggleTicketType = (body: ToggleTicketTypeRequest): Promise<TicketTypeDetail> =>
  post<TicketTypeDetail>(`${BASE}/toggle`, body);
