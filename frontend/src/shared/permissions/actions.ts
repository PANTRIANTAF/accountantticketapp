/**
 * The action-name union. THIRTY-FIVE names, in the order GeneralUIArchitecture.md section 6.1
 * prints them, mirroring the union of the SIX AccountantApp.Api/Slices/*ActionCatalogue.cs files
 * whose slices have a UI plan:
 *
 *   Audit/AuditActionCatalogue.cs:13                  1
 *   Customers/CustomersActionCatalogue.cs:13-22       8
 *   Employees/EmployeesActionCatalogue.cs:22-63      13
 *   Identity/IdentityActionCatalogue.cs:24-29         6
 *   Notifications/NotificationsActionCatalogue.cs:13-14   2
 *   TicketTypes/TicketTypesActionCatalogue.cs:13-18   5
 *                                                    --
 *                                                    35
 *
 * COPY SECTION 6.1 AND ADD NOTHING. Not a name from a screen spec, not one found in a handler, not
 * `UploadDocument`, not a plural that reads better. The reason is mechanical: PermissionChecker is
 * FAIL-CLOSED on an unrecognised action name and can()'s `?? false` mirrors it. A name here that
 * no catalogue declares makes the UI draw a button that 403s for everybody and writes a false
 * PermissionDenied audit row on every click.
 *
 * DO NOT RE-DERIVE THIS FROM THE GLOB. `Slices/*ActionCatalogue.cs` resolves to SEVEN files and 57
 * names. The seventh is Slices/Tickets/TicketsActionCatalogue.cs:33-83 -- the eighteen ticket
 * actions plus the four Documents actions it registers on that slice's behalf -- and its 22 names
 * MUST NOT APPEAR HERE. They are not missing: there is no Tickets UI plan and no screen behind any
 * of them. Thirty-five is the number to satisfy.
 *
 * Login, logout, /api/auth/me and change-password are deliberately NOT actions.
 * IdentityActionCatalogue.cs: "An entry listing all four roles would imply a role decision where
 * there is not one, and would be a check that can only ever pass."
 *
 * A slice plan ADDS ROWS to this union and to ACTIONS in can.ts, in the same commit as the server
 * action. It never replaces either.
 */
export type ActionName =
  // Audit (1)
  | 'ReadAuditLog'
  // Customers (8)
  | 'CreateCustomer'
  | 'SuspendCustomer'
  | 'ReactivateCustomer'
  | 'ListCustomers'
  | 'EditCustomerLegal'
  | 'EditCustomerContact'
  | 'ViewCustomer'
  | 'ViewOwnCustomer'
  // Employees (13)
  | 'OnboardCustomer'
  | 'RegisterEmployee'
  | 'ListEmployees'
  | 'ViewEmployee'
  | 'UpdateEmployee'
  | 'UpdateOwnContact'
  | 'InviteEmployee'
  | 'SetEmployeeRole'
  | 'DepartEmployee'
  | 'ReinstateEmployee'
  | 'ChangeEmployeeLoginEmail'
  | 'SuspendEmployeeAccount'
  | 'ReactivateEmployeeAccount'
  // Identity (6)
  | 'ListAccountants'
  | 'InviteAccountant'
  | 'SuspendAccountant'
  | 'ReactivateAccountant'
  | 'PromoteAccountant'
  | 'DemoteAccountant'
  // Notifications (2)
  | 'ReadOwnNotifications'
  | 'MarkOwnNotificationRead'
  // TicketTypes (5)
  | 'CreateTicketType'
  | 'EditTicketType'
  | 'ToggleTicketType'
  | 'ReadTicketType'
  | 'ListTicketTypes';
