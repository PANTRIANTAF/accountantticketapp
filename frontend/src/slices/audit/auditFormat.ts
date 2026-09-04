import { parseUtc } from '../../shared/format/dates';
import { ROLE_LABELS, UserRole } from '../../shared/format/enums';

/**
 * Display helpers for audit rows. Every one of them exists because an audit reader has a stricter
 * requirement than the rest of the app, and none of them re-implements a Phase 0 decision -- with
 * one documented exception, formatOccurredAt, explained on it.
 */

/**
 * A DOCUMENTED PHASE 0 GAP, REPORTED RATHER THAN HIDDEN.
 *
 * AuditScreens.md section 6 rule G and this slice's plan step 4 rule K require date, time AND
 * SECONDS, through shared/format/dates.ts. Phase 0's dateTimeFormatter (dates.ts:66-72) declares
 * year, month, day, hour and minute and NO `second`, and AuditScreens.md's own files checklist
 * lists "shared/format/dates.ts -- the exact date-time-seconds formatter" as a file this slice
 * needs. It was not delivered, and this slice may not write under shared/.
 *
 * SECONDS ARE LOAD-BEARING, NOT DECORATION. The server orders occurredAt DESC, id DESC precisely
 * because one transaction writes several entries in the same second
 * (SearchAuditLogHandler.cs:62-65), so a minute-precision column renders two rows in a fixed order
 * it cannot justify. Dropping them to stay inside Phase 0's formatter would break the higher-
 * precedence document; so the PARSE -- the timezone-sensitive half, and the whole reason section
 * 10.2 wants one module -- still comes from shared/format/dates.ts, and only the Intl options are
 * local. Move both lines into dates.ts as `formatDateTimeWithSeconds` and delete this function.
 *
 * NEVER RELATIVE. "3 hours ago" is useless in an investigation, changes on every re-render, and
 * renders two entries forty minutes apart identically (section 10.2, AuditScreens.md section 6
 * rule G). There is no relative-time formatter anywhere under slices/audit/.
 */
const occurredAtFormatter = new Intl.DateTimeFormat(undefined, {
  year: 'numeric',
  month: 'short',
  day: '2-digit',
  hour: '2-digit',
  minute: '2-digit',
  second: '2-digit',
});

export function formatOccurredAt(value: string | null | undefined): string {
  const parsed = parseUtc(value);
  return parsed === null ? EM_DASH : occurredAtFormatter.format(parsed);
}

/**
 * The one placeholder character, used for both "" and null.
 *
 * They are different facts about the row -- customerId: null means no Customer was involved, while
 * targetId: "" means nothing was recorded -- but neither is a value a reader can act on, and both
 * render as an em dash. "All Customers" for a null customerId would invert the meaning of the most
 * sensitive column on the screen (AuditScreens.md section 6 rule C).
 */
export const EM_DASH = '—';

export function dashIfEmpty(value: string | null): string {
  return value === null || value.trim() === '' ? EM_DASH : value;
}

/**
 * Ids are shown SHORTENED IN THE TABLE and in full on the detail screen, with no lookup anywhere:
 * there is no id-to-name endpoint (punch-list item 23) and building a best-effort resolver would
 * make a name appear for some rows and not others, which reads as a data-quality problem in the log
 * rather than a gap in the client.
 *
 * The full value goes in a `title` so a hover still yields the whole id; it is never parsed,
 * re-formatted or trimmed of anything but the middle, and the ellipsis marks the UI's own
 * shortening -- distinct from the server's write-time truncation, which is rendered verbatim with
 * no marker at all (plan section 11.4).
 */
export function middleTruncate(value: string, head = 8, tail = 4): string {
  if (value.trim() === '') return EM_DASH;
  if (value.length <= head + tail + 1) return value;
  return `${value.slice(0, head)}…${value.slice(-tail)}`;
}

/**
 * Thousands separators, because 412338 read as a page count is how a reader concludes the log is
 * corrupt (AuditScreens.md section 3.5 rule B, section 10.2).
 */
const countFormatter = new Intl.NumberFormat();

export function formatCount(value: number): string {
  return countFormatter.format(value);
}

/**
 * THE ROLE ON AN AUDIT ROW IS A STRING, WHILE `role` EVERYWHERE ELSE IN THE API IS AN INTEGER.
 * AuditApi.cs:35 stores user.Role.ToString(); LogUnauthenticatedAsync stores the literal "Unknown"
 * (:22), which is not a UserRole at all. So shared/format/enums.ts's ROLE_LABELS -- keyed by the
 * integer -- cannot be indexed with it, and Number(actorRole) is NaN.
 *
 * The labels themselves still come from ROLE_LABELS: this maps the C# enum NAME to the same
 * glossary text, so "Accountant Admin" is spelled identically here and everywhere else, and the
 * banned bare word "Admin" cannot creep in.
 *
 * AN UNRECOGNISED VALUE IS RENDERED VERBATIM, NEVER BLANK -- including "Unknown". A role this UI
 * does not know is itself information (AuditScreens.md section 6 rule B).
 */
const AUDIT_ROLE_LABELS: Record<string, string> = {
  AccountantAdmin: ROLE_LABELS[UserRole.AccountantAdmin],
  AccountantUser: ROLE_LABELS[UserRole.AccountantUser],
  CustomerAdmin: ROLE_LABELS[UserRole.CustomerAdmin],
  Employee: ROLE_LABELS[UserRole.Employee],
};

export function auditRoleLabel(actorRole: string): string {
  if (actorRole.trim() === '') return EM_DASH;
  return AUDIT_ROLE_LABELS[actorRole] ?? actorRole;
}

/**
 * Link a target ONLY where an SPA route exists (AuditScreens.md section 6 rule E, plan step 7
 * rule D). AuditTargets.cs:74-81 declares eight kinds; five of them have no screen in this
 * application, so a link would render NotFoundPage and read as a broken audit log.
 *
 * Returns null for Ticket, Document, Notification, None, UserAccount and for an empty targetId.
 * UserAccount is deliberately in that list: /accountants is a LIST screen with no per-account
 * route, and a UserAccount id is not an Employee id.
 */
export function targetRoute(targetKind: string, targetId: string): string | null {
  if (targetId.trim() === '') return null;
  switch (targetKind) {
    case 'Customer':
      return `/customers/${targetId}`;
    case 'Employee':
      return `/employees/${targetId}`;
    case 'TicketType':
      return `/ticket-types/${targetId}`;
    default:
      return null;
  }
}
