import * as z from 'zod';
import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE } from '../../../shared/api/paginated';
import { parseUtc } from '../../../shared/format/dates';
import { formatOccurredAt } from '../auditFormat';
import { isGuid } from '../guid';
import type { AuditSearchRequest } from '../types';

/**
 * The filter schema, and the two conversions either side of it: form values <-> AuditSearchRequest
 * <-> URL search params. It lives beside the screen that uses it, not in shared/ (section 9.1).
 *
 * TEN FIELDS, because SearchAuditLogRequestDto.cs declares ten. The panel renders controls for the
 * eight filters only -- paging belongs to the pager -- but pageNumber and pageSize travel through
 * the form so the applied filter set is one object with one validator, and the values in the URL are
 * validated by the same rules as the values a user typed.
 *
 * THIS SCHEMA IS THE ONLY THING THAT CAN PUT A MESSAGE NEXT TO A CONTROL. The API's ProblemDetails
 * carries no field-level errors (punch-list item 5), so a 422 is one sentence with nothing tying it
 * to an input (section 7.3). A rule stricter than the server's blocks legitimate input; a looser one
 * produces a field-less banner. These two mirror two server behaviours exactly and add nothing.
 */

const CUSTOMER_ID_MESSAGE =
  'Enter a complete customer id, or clear the field. A partial id cannot be searched for.';

const DATE_ORDER_MESSAGE = 'The "to" date must not be earlier than the "from" date.';

export const auditFilterSchema = z
  .object({
    // Exact, case-sensitive equality (SearchAuditLogHandler.cs:42). No length or format rule: the
    // value may be any identifier that was attempted, including one matching no row anywhere.
    actorUserId: z.string(),

    // The three catalogue filters carry no client rule. Their 422s (:95, :103, :107) are unreachable
    // while the values come from server-populated Selects, and are reachable only where the
    // catalogue failed and the controls degraded to text fields -- in which case the server's own
    // sentence is rendered verbatim, and one of those sentences names the endpoint to fetch.
    action: z.string(),
    targetKind: z.string(),
    outcome: z.string(),

    // 'TargetId' requires 'TargetKind' (:92) is enforced STRUCTURALLY instead of here: the control
    // is disabled until a kind is chosen and clearing the kind clears the id, so the invalid state
    // cannot be expressed and the 422 cannot be reached.
    targetId: z.string(),

    // RULE B. customerId binds to a Guid? (SearchAuditLogRequestDto.cs:18), so a non-GUID is a
    // model-binding 400 -- not a 422 -- and a 400 carries no sentence worth rendering.
    customerId: z.string().refine((value) => value.trim() === '' || isGuid(value.trim()), {
      message: CUSTOMER_ID_MESSAGE,
    }),

    // Date objects, so the pickers own one representation and toSearchRequest is the only place an
    // offset is written (see toSearchRequest).
    from: z.date().nullable(),
    to: z.date().nullable(),

    pageNumber: z.number().int().min(1),
    pageSize: z.number().int().min(1).max(MAX_PAGE_SIZE),
  })
  // RULE A. The server answers 422 "'From' must not be later than 'To'."
  // (SearchAuditLogHandler.cs:85-86) with NO field attached, so it can only be rendered as a
  // banner. Caught here it outlines the offending picker, which is the only mechanism that can.
  .refine((values) => values.from === null || values.to === null || values.from <= values.to, {
    path: ['to'],
    message: DATE_ORDER_MESSAGE,
  });

export type AuditFilterValues = z.infer<typeof auditFilterSchema>;

/** The eight filters, in panel order. Paging is not a filter and is not removable. */
export const AUDIT_FILTER_FIELDS = [
  'actorUserId',
  'action',
  'outcome',
  'targetKind',
  'targetId',
  'customerId',
  'from',
  'to',
] as const;

export type AuditFilterField = (typeof AUDIT_FILTER_FIELDS)[number];

/** Control labels, reused verbatim by the collapsed panel's chips and by the empty state. */
export const AUDIT_FILTER_LABELS: Record<AuditFilterField, string> = {
  actorUserId: 'Actor user id',
  action: 'Action',
  outcome: 'Outcome',
  targetKind: 'Target kind',
  targetId: 'Target id',
  customerId: 'Customer id',
  from: 'From',
  to: 'To',
};

/** No filters, page one. All eight absent means "the whole log, most recent page first". */
export const emptyAuditSearchRequest = (): AuditSearchRequest => ({
  actorUserId: null,
  action: null,
  targetKind: null,
  targetId: null,
  customerId: null,
  outcome: null,
  from: null,
  to: null,
  pageNumber: 1,
  pageSize: DEFAULT_PAGE_SIZE,
});

/**
 * Form values -> request. Two rules meet here:
 *
 *   SEND null, NOT '' (section 9.3 rule F). The seven string filters are tested with
 *   IsNullOrWhiteSpace so '' happens to behave as absent -- but customerId is a Guid? and '' is a
 *   400 from binding. Sending null for every untouched field means that asymmetry never arises.
 *
 *   SEND AN EXPLICIT OFFSET, via toISOString() (section 3.2 rule H). A bare local datetime is bound
 *   against the SERVER's offset, silently shifting the window and dropping hours of evidence at the
 *   boundary.
 *
 * Values are trimmed first (section 9.3 rule E): a trailing space makes an exact-equality filter
 * match nothing at all.
 */
export function toSearchRequest(values: AuditFilterValues): AuditSearchRequest {
  return {
    actorUserId: nullIfBlank(values.actorUserId),
    action: nullIfBlank(values.action),
    targetKind: nullIfBlank(values.targetKind),
    // Structurally impossible to set without a kind; belt and braces, because a hand-edited URL is
    // the one path that bypasses the control's disabled state.
    targetId: nullIfBlank(values.targetKind) === null ? null : nullIfBlank(values.targetId),
    customerId: nullIfBlank(values.customerId),
    outcome: nullIfBlank(values.outcome),
    from: values.from === null ? null : values.from.toISOString(),
    to: values.to === null ? null : values.to.toISOString(),
    pageNumber: values.pageNumber,
    pageSize: values.pageSize,
  };
}

/** Request -> form values, for seeding the panel from the URL. */
export function toFilterValues(request: AuditSearchRequest): AuditFilterValues {
  return {
    actorUserId: request.actorUserId ?? '',
    action: request.action ?? '',
    targetKind: request.targetKind ?? '',
    targetId: request.targetId ?? '',
    customerId: request.customerId ?? '',
    outcome: request.outcome ?? '',
    from: toDate(request.from),
    to: toDate(request.to),
    pageNumber: request.pageNumber,
    pageSize: request.pageSize,
  };
}

/**
 * URL -> request. The applied filters live in the /audit query string and the query key derives
 * from them (section 3.2 rule E): otherwise Back from an entry re-runs an unfiltered search and the
 * investigator loses their place in a table with hundreds of thousands of rows. It is also what
 * makes a search shareable, which is how one Admin hands an investigation to another.
 *
 * NOTHING IS SILENTLY DROPPED HERE. A malformed value from a hand-edited or truncated link is kept
 * and reported by auditFilterIssue() below, because a filter that vanished on the way in would
 * leave the panel showing a filter the table did not apply -- the reader would take a whole-log
 * result for a filtered one, which is the misreading this screen exists to avoid.
 */
export function parseAuditSearchParams(params: URLSearchParams): AuditSearchRequest {
  const text = (key: AuditFilterField): string | null => nullIfBlank(params.get(key) ?? '');
  const targetKind = text('targetKind');

  return {
    actorUserId: text('actorUserId'),
    action: text('action'),
    targetKind,
    targetId: targetKind === null ? null : text('targetId'),
    customerId: text('customerId'),
    outcome: text('outcome'),
    from: text('from'),
    to: text('to'),
    pageNumber: positiveInteger(params.get('pageNumber')) ?? 1,
    // Clamped, never offered above what the server honours. usePaginatedQuery clamps again.
    pageSize: Math.min(positiveInteger(params.get('pageSize')) ?? DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE),
  };
}

/** Request -> URL. Absent filters are absent parameters, so a clean search has a clean URL. */
export function toAuditSearchParams(request: AuditSearchRequest): URLSearchParams {
  const params = new URLSearchParams();
  for (const field of AUDIT_FILTER_FIELDS) {
    const value = request[field];
    if (value !== null) params.set(field, value);
  }
  if (request.pageNumber !== 1) params.set('pageNumber', String(request.pageNumber));
  if (request.pageSize !== DEFAULT_PAGE_SIZE) params.set('pageSize', String(request.pageSize));
  return params;
}

/**
 * The active filters, for the collapsed panel's chips (section 3.2 rule C) and for the empty state
 * (section 3.5 rule C). A collapsed panel reading only "Filters", or a bare "No results", lets a
 * reader take a filtered table for the whole log and conclude "this never happened" from rows that
 * were merely excluded.
 */
export function activeFilters(
  request: AuditSearchRequest,
): { field: AuditFilterField; label: string; value: string }[] {
  return AUDIT_FILTER_FIELDS.flatMap((field) => {
    const value = request[field];
    if (value === null) return [];
    return [
      {
        field,
        label: AUDIT_FILTER_LABELS[field],
        value: field === 'from' || field === 'to' ? formatOccurredAt(value) : value,
      },
    ];
  });
}

/**
 * The one sentence for a filter set that cannot be sent -- reachable only from a hand-edited,
 * truncated or mistyped link, because the schema catches both cases inside the panel.
 *
 * It exists so the screen can refuse to search rather than provoke a 400 (a non-GUID customerId
 * binds before any handler runs) or a 422 whose sentence has no field to sit next to.
 */
export function auditFilterIssue(request: AuditSearchRequest): string | null {
  if (request.customerId !== null && !isGuid(request.customerId)) {
    return CUSTOMER_ID_MESSAGE;
  }

  const from = toDate(request.from);
  const to = toDate(request.to);
  if (request.from !== null && from === null) return 'The "from" date in this link is not a date.';
  if (request.to !== null && to === null) return 'The "to" date in this link is not a date.';
  if (from !== null && to !== null && from > to) return DATE_ORDER_MESSAGE;

  return null;
}

function nullIfBlank(value: string): string | null {
  const trimmed = value.trim();
  return trimmed === '' ? null : trimmed;
}

function positiveInteger(value: string | null): number | null {
  if (value === null) return null;
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed >= 1 ? parsed : null;
}

/**
 * Both filter bounds cross the wire as DateTimeOffset -- an ISO string carrying an offset
 * (section 10.2) -- so parsing is Phase 0's parseUtc and not a second `new Date` here: it returns
 * null rather than an Invalid Date, and a hand-edited link with no offset is read as UTC instead of
 * as the reader's local time, which is the silent-eight-hour-shift dates.ts exists to prevent.
 */
function toDate(value: string | null): Date | null {
  return parseUtc(value);
}
