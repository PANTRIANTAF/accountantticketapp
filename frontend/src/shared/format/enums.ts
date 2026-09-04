/**
 * Mirrors Shared/Auth/UserRole.cs. THE DECLARATION ORDER IS THE WIRE CONTRACT: no
 * JsonStringEnumConverter is registered, so every C# enum serialises as its integer.
 * BACKEND_CHANGES_REQUIRED item 4.
 *
 * A. AccountantAdmin is 0, and 0 is falsy. `if (session.role)`, `role || fallback` and
 *    `role ? label : 'unknown'` are all wrong for the most privileged role in the system.
 *    Compare with === against a named constant, always.
 * B. Never send a role as a string. InviteAccountantRequestDto.Role and SetEmployeeRoleRequestDto
 *    bind an enum; "AccountantUser" is a 400 from model binding, before any handler runs, so with
 *    no useful message.
 * C. Never render the raw number. ROLE_LABELS is the only source of role text.
 */
export const UserRole = {
  AccountantAdmin: 0,
  AccountantUser: 1,
  CustomerAdmin: 2,
  Employee: 3,
} as const;

export type UserRole = (typeof UserRole)[keyof typeof UserRole];

/**
 * Labels come from 00-Glossary.md and are NOT the C# names: AccountantAdmin displays as
 * "Accountant Admin". The bare word "Admin" is banned -- it is ambiguous between AccountantAdmin
 * and CustomerAdmin.
 */
export const ROLE_LABELS: Record<UserRole, string> = {
  [UserRole.AccountantAdmin]: 'Accountant Admin',
  [UserRole.AccountantUser]: 'Accountant User',
  [UserRole.CustomerAdmin]: 'Customer Admin',
  [UserRole.Employee]: 'Employee',
};

/**
 * Four status vocabularies, no two of them the same, and `Invited` belongs to exactly one of them.
 * GeneralUIArchitecture.md section 10.1.
 *
 * These are STRING unions, not integer enums: they are already `string` in their C# DTOs, so they
 * cross the wire as strings while `role` crosses as a number, sometimes in adjacent fields of the
 * same response. There is no rule to learn -- it has to be checked per field.
 */

/**
 * A Customer is NEVER `Invited`. CustomerStatus declares only these two, both insert paths write
 * `Active`, ListCustomersHandler answers 422 "Unknown customer status." for anything else, and
 * migration 20260901_002_AddCustomerStatusCheck.sql adds
 * CHECK (status IN ('Active', 'Suspended')). A Customer status filter offers TWO options.
 */
export type CustomerStatus = 'Active' | 'Suspended';

/** A UserAccount status -- the person, not the company. This is the one vocabulary with `Invited`. */
export type AccountStatus = 'Invited' | 'Active' | 'Suspended';

/** An Employee record's status. Note there is no `Suspended` here: that belongs to the account. */
export type EmployeeStatus = 'Active' | 'Departed';

/** An audit entry's outcome. */
export type AuditOutcome = 'Success' | 'Denied' | 'Failure';
