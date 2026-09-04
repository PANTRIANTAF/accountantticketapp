import { UserRole } from '../format/enums';
import type { ActionName } from './actions';

/**
 * Mirrors the union of the SIX AccountantApp.Api/Slices/*ActionCatalogue.cs files whose slices
 * have a UI plan: Audit, Customers, Employees, Identity, Notifications, TicketTypes.
 *
 * Do not re-derive this from the glob. AccountantApp.Api/Slices/*ActionCatalogue.cs now resolves
 * to SEVEN files and 57 names; the seventh is Slices/Tickets/TicketsActionCatalogue.cs and its
 * 22 names must not appear here.
 *
 * Governed by 02-AuthorizationMatrix.md. When an action is added on the server, add it here in
 * the same commit; a missing row denies (see can()), so the UI hides a button the user is
 * entitled to -- annoying, and much safer than the reverse.
 *
 * THE TWO ROWS A BUILDER TIDIES, AND MUST NOT. ReinstateEmployee includes CustomerAdmin;
 * ChangeEmployeeLoginEmail does NOT (EmployeesActionCatalogue.cs:60). 02-AuthorizationMatrix.md
 * section 4 gives the reasons: a Customer Admin who can enter a departure must be able to correct
 * one, and "changing a login email is reserved to the Office, and nobody may change their own".
 * can(CustomerAdmin, 'ChangeEmployeeLoginEmail') === false BY DESIGN. It is not a missing row.
 *
 * The catalogue's CustomerAdmin grants are often "Yes, own Customer", and no catalogue entry
 * anywhere can express that scope. Row-level scoping stays in the handler, enforced by
 * CustomerScope, and surfaces as a 404. A can() of true NEVER means "this particular record".
 */
const ACTIONS: Record<ActionName, UserRole[]> = {
  // ----- Audit (1) -----
  ReadAuditLog: [UserRole.AccountantAdmin],

  // ----- Customers (8) -----
  CreateCustomer: [UserRole.AccountantAdmin],
  SuspendCustomer: [UserRole.AccountantAdmin],
  ReactivateCustomer: [UserRole.AccountantAdmin],
  ListCustomers: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  EditCustomerLegal: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  EditCustomerContact: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  ViewCustomer: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  ViewOwnCustomer: [UserRole.CustomerAdmin, UserRole.Employee],

  // ----- Employees (13) -----
  OnboardCustomer: [UserRole.AccountantAdmin],
  RegisterEmployee: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  ListEmployees: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  ViewEmployee: [
    UserRole.AccountantAdmin,
    UserRole.AccountantUser,
    UserRole.CustomerAdmin,
    UserRole.Employee,
  ],
  UpdateEmployee: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  UpdateOwnContact: [UserRole.CustomerAdmin, UserRole.Employee],
  InviteEmployee: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  SetEmployeeRole: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  DepartEmployee: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  ReinstateEmployee: [UserRole.AccountantAdmin, UserRole.AccountantUser, UserRole.CustomerAdmin],
  ChangeEmployeeLoginEmail: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  SuspendEmployeeAccount: [
    UserRole.AccountantAdmin,
    UserRole.AccountantUser,
    UserRole.CustomerAdmin,
  ],
  ReactivateEmployeeAccount: [
    UserRole.AccountantAdmin,
    UserRole.AccountantUser,
    UserRole.CustomerAdmin,
  ],

  // ----- Identity (6) -----
  ListAccountants: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  InviteAccountant: [UserRole.AccountantAdmin],
  SuspendAccountant: [UserRole.AccountantAdmin],
  ReactivateAccountant: [UserRole.AccountantAdmin],
  PromoteAccountant: [UserRole.AccountantAdmin],
  DemoteAccountant: [UserRole.AccountantAdmin],

  // ----- Notifications (2) -----
  ReadOwnNotifications: [
    UserRole.AccountantAdmin,
    UserRole.AccountantUser,
    UserRole.CustomerAdmin,
    UserRole.Employee,
  ],
  MarkOwnNotificationRead: [
    UserRole.AccountantAdmin,
    UserRole.AccountantUser,
    UserRole.CustomerAdmin,
    UserRole.Employee,
  ],

  // ----- TicketTypes (5) -----
  CreateTicketType: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  EditTicketType: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  ToggleTicketType: [UserRole.AccountantAdmin, UserRole.AccountantUser],
  ReadTicketType: [
    UserRole.AccountantAdmin,
    UserRole.AccountantUser,
    UserRole.CustomerAdmin,
    UserRole.Employee,
  ],
  ListTicketTypes: [
    UserRole.AccountantAdmin,
    UserRole.AccountantUser,
    UserRole.CustomerAdmin,
    UserRole.Employee,
  ],
};

/**
 * The one permission question in the client. Five rules from GeneralUIArchitecture.md section 6.2:
 *
 * A. can() decides AFFORDANCES, never data. 02-AuthorizationMatrix.md:311 -- "Never rely on the
 *    React app to hide data. Internal Notes, Accountant-only fields, and out-of-scope records must
 *    be ABSENT FROM THE API RESPONSE, not merely unrendered." If the UI is filtering rows or fields
 *    for security, the server has already leaked them and the UI is concealing a live bug.
 * B. can() returning true followed by a 403 is a bug in THIS TABLE, not on the server. Fix the row;
 *    do not add a try/catch that swallows the 403. The server audits every denial.
 * C. Prefer hiding to disabling. A button a user can never enable is noise.
 * D. The table says WHO MAY CALL, not WHICH ROWS. See the ACTIONS comment.
 * E. Never persist or cache a decision. This is a pure function of the role; recompute it.
 */
export function can(role: UserRole | undefined, action: ActionName): boolean {
  if (role === undefined) return false; // no session: nothing is permitted
  return ACTIONS[action]?.includes(role) ?? false; // unknown action: deny, matching the server
}
