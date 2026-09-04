import type { Notification } from './types';

/**
 * The whole event-kind mapping, in ONE file, so adding a kind is one edit.
 * Transcribed row for row from NotificationsScreens.md section 5's table, in its order.
 *
 * The label is a CATEGORY LABEL rendered as the row's kind chip. It is NOT the row's text: the
 * server's `title` and `body` are written by the producing handler for this reader and render
 * verbatim beside it (section 4.4 rule B).
 *
 * A Record<string, string>, deliberately not a Record over a union of the eighteen kinds:
 * `eventKind` crosses the wire as an arbitrary string, so the lookup must accept one.
 * noUncheckedIndexedAccess makes the result `string | undefined`, which is what forces the caller's
 * `?? eventKind` fallback to exist rather than be dead code.
 */
export const EVENT_LABELS: Record<string, string> = {
  // --- Identity and Employees (six kinds, none of them about a ticket) ---
  Invited: 'Invitation',
  EmployeeInvited: 'Invitation',
  PasswordResetRequested: 'Password reset',
  EmployeeRegistered: 'Employee registered',
  EmployeeDeparted: 'Employee departed',
  AccountSuspended: 'Account suspended',

  // --- Tickets, to the Customer side (NotificationEvents.cs:11-17) ---
  TicketPickedUp: 'Ticket picked up',
  InformationRequested: 'Information requested',
  FieldRejected: 'Field rejected',
  TicketAnswered: 'Ticket answered',
  TicketClosed: 'Ticket closed',
  TicketCancelled: 'Ticket cancelled',
  AccountantResponded: 'Accountant responded',

  // --- Tickets, to the Office (NotificationEvents.cs:20-24) ---
  TicketSubmitted: 'Ticket submitted',
  CorrectionSubmitted: 'Correction submitted',
  CustomerReplied: 'Customer replied',
  TicketAssignedToYou: 'Assigned to you',
  DueDateApproaching: 'Due date approaching',
};

/**
 * The TWELVE ticket-related kinds, read off NotificationEvents.cs:11-24 (its two "Tickets" sections,
 * seven kinds to the Customer side and five to the Office). Twelve plus the six above is the eighteen
 * that NotificationEvents.All contains.
 *
 * They are set apart from the other six because they are the ones whose row carries the
 * TICKET_UNAVAILABLE_NOTE below: the other six are linkless BY DESIGN -- their tokens live only in the
 * email, or the payload carries a name and no id -- while these twelve are linkless because this SPA
 * has nowhere to send the reader yet.
 *
 * Membership is decided by the kind, not by `ticketId`: a row may carry a ticketId this UI must never
 * render (section 4.4 rule F), and a producer that ever omits it must not silently change how the row
 * reads.
 */
const TICKET_EVENT_KINDS: ReadonlySet<string> = new Set([
  'TicketPickedUp',
  'InformationRequested',
  'FieldRejected',
  'TicketAnswered',
  'TicketClosed',
  'TicketCancelled',
  'AccountantResponded',
  'TicketSubmitted',
  'CorrectionSubmitted',
  'CustomerReplied',
  'TicketAssignedToYou',
  'DueDateApproaching',
]);

export function isTicketEventKind(eventKind: string): boolean {
  return TICKET_EVENT_KINDS.has(eventKind);
}

/**
 * The one muted line a ticket notification carries under its body (section 5 rule A).
 *
 * It is not an apology and not a placeholder to be improved into a link: inventing /tickets/:id ships
 * twelve dead links that render NotFoundPage and read as a BROKEN application, while this note reads
 * as an UNFINISHED one -- which is true.
 */
export const TICKET_UNAVAILABLE_NOTE = 'Not available yet — ticket screens do not exist';

/**
 * ONE PLACE HOLDS THE FUTURE LINK, and today it returns null for every input (section 5 rule C).
 *
 * WHY EVERY KIND IS LINKLESS RIGHT NOW, which is two different reasons and neither is this function
 * being unfinished:
 *
 *   The twelve ticket kinds have no CLIENT ROUTE. The backend slice is built -- Slices/Tickets is
 *   registered and routed, and fifteen of the API's twenty-one notification producers live under it,
 *   so these rows genuinely arrive -- but there is no Screens/TicketsScreens.md, no Tickets UI plan,
 *   and therefore no route in routes.tsx and no screen to navigate into.
 *
 *   The six non-ticket kinds are linkless BY DESIGN. Invited and EmployeeInvited are accepted through
 *   the emailed /accept-invitation link; PasswordResetRequested's token is in the email only and the
 *   stored body deliberately holds none; AccountSuspended belongs to an account login now rejects;
 *   and EmployeeRegistered / EmployeeDeparted carry the person's NAME and not their id, so there is
 *   nothing to build /employees/:employeeId from. Section 5 rule E forbids resolving the name by
 *   searching the employee list -- two employees may share a name, and a wrong link is worse than
 *   none. Carrying the id is a backend change, recorded as BACKEND_CHANGES_REQUIRED item 31.
 *
 * IT STAYS A FUNCTION rather than being inlined as null. When a Tickets UI ships, the ticket-bearing
 * kinds return the route that Screens/TicketsScreens.md names -- read from that document, never
 * guessed here -- and that is a one-line change in this one file, because NotificationRow already
 * branches on the result.
 */
export function destinationFor(notification: Notification): string | null {
  // Referenced so the parameter survives noUnusedParameters, in the shape SessionProvider.tsx:114
  // already uses. The parameter is part of the signature this function will need on the day it
  // returns something.
  void notification;
  return null;
}
