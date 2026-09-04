import type { AccountStatus, EmployeeStatus, UserRole } from '../../shared/format/enums';

/**
 * WIRE TYPES for the Employees slice. Hand-written against the C# DTOs, camelCase, `Guid` -> string,
 * each one commented with the file it mirrors so the next reader can diff the two side by side
 * (GeneralUIArchitecture.md section 2.5). There is no OpenAPI document and no generated client:
 * producing one is BACKEND_CHANGES_REQUIRED item 9, and a generator would be WRONG here anyway,
 * because /api/employees/get declares one of the two shapes it returns (item 6).
 *
 * THREE READ SHAPES, NOT ONE. EmployeeReadDtos.cs explains why they are three types rather than one
 * with nulled-out fields: "a type that has no SocialSecurityNumber property cannot serialise one."
 * 02-AuthorizationMatrix.md section 4's "View an Employee record" row has three different answers,
 * so there are three DTOs.
 *
 * A. NO UNION AND NO OPTIONAL-EVERYTHING SUPERSET. `EmployeeDetail | EmployeeSelf` forces a
 *    narrowing check at every field access, and that check is a field sniff wearing a type
 *    annotation (EmployeesScreens.md section 2.3 rules A and C). The shape is chosen from the
 *    session role BEFORE the call, in queries.ts.
 *
 * B. `status` IS A STRING AND `role` IS AN INTEGER, in the same payload. No JsonStringEnumConverter
 *    is registered, so C# enums serialise as integers while properties already declared `string`
 *    serialise as strings -- two conventions with nothing in the JSON marking the difference
 *    (BACKEND_CHANGES_REQUIRED item 4). `role` is additionally NULLABLE, and `AccountantAdmin` is
 *    `0`, which is falsy: never test `role` for truthiness and never use `role || fallback`.
 *    Distinguish `null` (never invited) from `0` explicitly.
 *
 * C. BOTH EMPLOYMENT DATES ARE `string`, "YYYY-MM-DD" -- a C# `DateOnly`, no timezone and no time.
 *    `createdAt` is also a string, from a `DateTimeOffset`, and carries an offset. Format them
 *    through shared/format/dates.ts: `new Date("2024-03-01")` parses as midnight UTC and prints as
 *    the previous day anywhere west of it (GeneralUIArchitecture.md section 10.2).
 *
 * D. NO ZOD IN THIS FILE. A validation schema is not a wire type; the schemas live in schemas.ts.
 */

// ---------------------------------------------------------------------------------------------
// Read shapes -- Slices/Employees/Application/Dtos/EmployeeReadDtos.cs
// ---------------------------------------------------------------------------------------------

/**
 * Mirrors `EmployeeSummaryDto` (EmployeeReadDtos.cs:18-34) -- the row shape of
 * POST /api/employees/list.
 *
 * SEVEN KEYS, AND THE ABSENCES ARE THE POINT. There is no `customerId`, which is the shape of the
 * API telling you not to filter rows in the browser (EmployeesScreens.md section 9 rule A); no
 * email, though `searchTerm` matches work email server-side, so the list searches a column it
 * cannot display; and no `accountStatus`, so `hasAccount: true` means an account EXISTS, never that
 * anybody can sign in with it. Never label that column "Active".
 */
export interface EmployeeSummary {
  id: string;
  givenName: string;
  familyName: string;
  jobTitle: string | null;
  /** "Active" | "Departed". Employment, not access -- two vocabularies sharing the word "Active". */
  status: EmployeeStatus;
  /** An account exists. It may be Invited or Suspended; this field cannot say which. */
  hasAccount: boolean;
  /**
   * A NULLABLE INTEGER. `null` renders "Not invited" -- never "Employee".
   * `EmployeeSummaryDto.Role`: "Do NOT default it to Employee: that would show every accountless
   * person as holding a role they do not have."
   */
  role: UserRole | null;
}

/**
 * Mirrors `EmployeeDetailDto` (EmployeeReadDtos.cs:41-63) -- what POST /api/employees/get returns
 * to an AccountantAdmin, an AccountantUser or a CustomerAdmin, and what every write except
 * update-own-contact returns.
 *
 * It carries the two personal identifying numbers, which are stored in plain text. If they arrive,
 * the caller was entitled to them (EmployeesScreens.md section 9 rule D) -- the per-field *Show*
 * toggle on the detail screen is ergonomics, not a security control.
 *
 * NO VERSION AND NO `updatedAt`. There is no optimistic concurrency anywhere in this backend: two
 * Admins editing one Employee both get 200 and the second write wins silently. Do not synthesise a
 * version from `createdAt` (EmployeesScreens.md section 5.5 rule F).
 */
export interface EmployeeDetail {
  id: string;
  customerId: string;
  givenName: string;
  familyName: string;
  jobTitle: string | null;
  workEmail: string | null;
  contactPhone: string | null;
  taxIdentificationNumber: string | null;
  socialSecurityNumber: string | null;
  /** DateOnly: "YYYY-MM-DD". */
  employmentStartDate: string;
  /** DateOnly or null. Non-null only on a Departed record. */
  employmentEndDate: string | null;
  /** DateTimeOffset, with an offset. */
  createdAt: string;
  /** EMPLOYMENT status: "Active" | "Departed". Owned by the employees table. */
  status: EmployeeStatus;
  hasAccount: boolean;
  /** Nullable integer. See rule B in the file header. */
  role: UserRole | null;
  /**
   * ACCESS status, owned by Identity: "Invited" | "Active" | "Suspended", or `null` when no account
   * exists. `null` renders "Not invited" -- not "Inactive" and not an empty chip
   * (EmployeesScreens.md section 5.3 rule C). Independent of `status` above: an Active Employee with
   * a Suspended account is a normal state, and neither may be inferred from the other.
   */
  accountStatus: AccountStatus | null;
}

/**
 * Mirrors `EmployeeSelfDto` (EmployeeReadDtos.cs:70-88) -- what POST /api/employees/get returns to
 * an `Employee`, for their OWN record only, and what update-own-contact returns.
 *
 * NARROWER ON THE SERVER, not merely unrendered. There is no `status`, no `hasAccount`, no `role`,
 * no `accountStatus`, no `employmentEndDate`, no `createdAt` and neither identifying number, so a
 * screen that renders a status chip from this shape renders `undefined`. An `Employee` has exactly
 * one readable employee record; every other id is a 404 by design (GetEmployeeHandler.cs:65).
 */
export interface EmployeeSelf {
  id: string;
  customerId: string;
  givenName: string;
  familyName: string;
  jobTitle: string | null;
  workEmail: string | null;
  contactPhone: string | null;
  /** DateOnly: "YYYY-MM-DD". */
  employmentStartDate: string;
  /**
   * NULL ON A READ. `EmployeeMapper.ToSelfExpression` never sets it; only `UpdateOwnContactHandler`
   * does, on a successful WRITE. A screen that shows the login-email warning when `notice` is
   * present shows it AFTER the mistake instead of before, which is exactly backwards -- render the
   * warning from static copy and surface `notice` in the success snackbar only
   * (EmployeesScreens.md section 7.5 rule B).
   */
  notice: string | null;
}

/**
 * Mirrors `MarkedResultDto` (EmployeeReadDtos.cs:94-97). One boolean and NO STATE, which is why the
 * six operations returning it cannot seed the cache and must invalidate instead: `set-role`,
 * `depart`, `reinstate`, `change-login-email`, `suspend-account`, `reactivate-account`
 * (EmployeesScreens.md section 1 note 4).
 */
export interface MarkedResult {
  success: boolean;
}

// ---------------------------------------------------------------------------------------------
// Request shapes -- EmployeeReadDtos.cs:99-124 and EmployeeWriteDtos.cs
// ---------------------------------------------------------------------------------------------

/**
 * Mirrors `ListEmployeesRequestDto` (EmployeeReadDtos.cs:99-118).
 *
 * `status` OMITTED OR null MEANS BOTH, and that is the correct default: the endpoint returns Active
 * and Departed unless filtered, and a client-side default of "Active" makes a Customer Admin think
 * a record is gone when nothing ever deletes an Employee. `''` is NOT "both" -- the handler trims
 * and compares case-sensitively, so `""` and `"active"` are both
 * 422 "Unknown employee status." (EmployeesScreens.md section 4.5 rules C and D).
 *
 * `customerId` is meaningful for the Accountant roles only. ListEmployeesHandler.cs:47-53 answers
 * 403 "You may only list employees at your own customer." when a CustomerAdmin names another
 * Customer, so the filter control is hidden for that role rather than drawn and not sent.
 */
export interface ListEmployeesRequest {
  customerId: string | null;
  status: EmployeeStatus | null;
  hasAccount: boolean | null;
  /** Matches given name, family name AND work email server-side. At most 200 characters. */
  searchTerm: string | null;
  pageNumber: number;
  /** Clamped to 50 by the server, not rejected. Render the pager from the response, never from this. */
  pageSize: number;
}

/** Mirrors `EmployeeIdRequestDto` (EmployeeReadDtos.cs:121-124). The id is in the BODY, never in a path. */
export interface EmployeeIdRequest {
  employeeId: string;
}

/**
 * Mirrors `RegisterEmployeeRequestDto` (EmployeeWriteDtos.cs:11-22).
 *
 * Creates an ACCOUNTLESS Employee: no login and no email (EmployeesEndpoints.cs:29). `workEmail` is
 * optional here and REQUIRED by /api/customers/onboard -- do not copy the onboarding form's
 * validation across (EmployeesScreens.md section 6.2 rule C).
 */
export interface RegisterEmployeeRequest {
  customerId: string;
  givenName: string;
  familyName: string;
  jobTitle: string | null;
  workEmail: string | null;
  contactPhone: string | null;
  taxIdentificationNumber: string | null;
  socialSecurityNumber: string | null;
  /** DateOnly: "YYYY-MM-DD". */
  employmentStartDate: string;
}

/**
 * Mirrors `UpdateEmployeeRequestDto` (EmployeeWriteDtos.cs:33-44).
 *
 * A FULL REPLACEMENT. The DTO says it outright: "omitting WorkEmail clears it." Every field is
 * required in the request object -- not optional -- precisely so no call site can send a partial
 * body and silently erase the tax identification number, the social-security number, the phone and
 * the work email with a 200, no warning and no undo (EmployeesScreens.md section 5.5 rule C).
 */
export interface UpdateEmployeeRequest {
  employeeId: string;
  givenName: string;
  familyName: string;
  jobTitle: string | null;
  workEmail: string | null;
  contactPhone: string | null;
  taxIdentificationNumber: string | null;
  socialSecurityNumber: string | null;
  employmentStartDate: string;
}

/**
 * Mirrors `UpdateOwnContactRequestDto` (EmployeeWriteDtos.cs:56-60).
 *
 * TWO FIELDS AND NO `employeeId`, DELIBERATELY. The DTO: "an EmployeeId here, however carefully
 * checked, turns every future edit of the handler into an opportunity to check it wrongly." The
 * endpoint is structurally incapable of editing a colleague -- not "checked", incapable. Never add
 * one (EmployeesScreens.md section 7.5 rule A).
 *
 * Also a full replacement of its two fields, so an unfilled submit erases both.
 */
export interface UpdateOwnContactRequest {
  workEmail: string | null;
  contactPhone: string | null;
}

/**
 * Mirrors `InviteEmployeeRequestDto` (EmployeeWriteDtos.cs:66-83).
 *
 * `loginEmail` is optional -- omitted, the handler uses the work email on file, and
 * 422 "No email address on file for this employee." when there is none. Whatever address is used
 * BECOMES the permanent login and is written back to `WorkEmail` as well: this is the only moment
 * in the person's life when that address is chosen (EmployeesScreens.md section 5.5 rule B).
 *
 * `role` is an INTEGER (a string is a 400 from model binding, before the handler runs) and must be
 * `CustomerAdmin` or `Employee`: EmployeeValidation.cs:110-114 answers
 * 422 "An Employee's role must be CustomerAdmin or Employee." for either Accountant role.
 */
export interface InviteEmployeeRequest {
  employeeId: string;
  loginEmail: string | null;
  role: UserRole;
}

/**
 * Mirrors `SetEmployeeRoleRequestDto` (EmployeeWriteDtos.cs:106-112). `role` is an integer;
 * `CustomerAdmin` is 2 and `Employee` is 3. Use the `UserRole` const, never a hand-typed literal.
 */
export interface SetEmployeeRoleRequest {
  employeeId: string;
  role: UserRole;
}

/**
 * Mirrors `DepartEmployeeRequestDto` (EmployeeWriteDtos.cs:122-132). The end date is REQUIRED, may
 * be in the future (a notice period is normal), and has no upper bound -- but the record flips to
 * Departed on submit regardless, so the UI must not imply the departure is scheduled.
 */
export interface DepartEmployeeRequest {
  employeeId: string;
  /** DateOnly: "YYYY-MM-DD". Not before `employmentStartDate`. */
  employmentEndDate: string;
}

/**
 * Mirrors `ChangeEmployeeLoginEmailRequestDto` (EmployeeWriteDtos.cs:94-104). Moves the address the
 * person SIGNS IN WITH; leaves the work email, the password and any live session alone. Accountant
 * roles only, and nobody may change their own -- there is no endpoint to build one against
 * (EmployeesScreens.md section 8.7).
 */
export interface ChangeEmployeeLoginEmailRequest {
  employeeId: string;
  loginEmail: string;
}

// ---------------------------------------------------------------------------------------------
// Onboarding -- POST /api/customers/onboard, registered by THIS slice and LOCKED
// ---------------------------------------------------------------------------------------------

/**
 * The `customer` half of `OnboardCustomerRequestDto`, mirroring `CreateCustomer`
 * (Slices/Customers/ExternalInterfaces/ICustomerApi.cs:18-32) -- TWELVE fields.
 *
 * Declared HERE rather than imported from slices/customers/types.ts on purpose. The onboarding wire
 * types are this slice's under 03-SliceInventory.md section 1 (the route is registered by
 * EmployeesEndpoints.cs), and the Customers slice imports this module rather than the reverse, so
 * the seam points one way only and no naming choice in that slice can break this one's compile.
 */
export interface OnboardCustomerCustomer {
  legalName: string;
  tradingName: string | null;
  taxNumber: string;
  taxOffice: string | null;
  addressLine1: string;
  addressLine2: string | null;
  addressCity: string;
  addressPostalCode: string;
  addressCountry: string;
  contactEmail: string;
  contactPhone: string;
  /** DateOnly: "YYYY-MM-DD". */
  onboardedOn: string;
}

/**
 * The `firstAdmin` half, mirroring `OnboardFirstAdminDto` (EmployeeWriteDtos.cs:156-166).
 *
 * `workEmail` IS REQUIRED HERE, unlike plain registration: this operation always invites, so the
 * address must exist (EmployeeValidation.cs:94-96 -- required, at most 320 characters, must contain
 * '@'). THERE IS NO `role` FIELD. `OnboardCustomerHandler` chooses `UserRole.CustomerAdmin` itself;
 * a `role` in this body would be ignored at best.
 */
export interface OnboardCustomerFirstAdmin {
  givenName: string;
  familyName: string;
  jobTitle: string | null;
  /** REQUIRED. Becomes the first Customer Admin's login address. */
  workEmail: string;
  contactPhone: string | null;
  taxIdentificationNumber: string | null;
  socialSecurityNumber: string | null;
  /** DateOnly: "YYYY-MM-DD". */
  employmentStartDate: string;
}

/**
 * Mirrors `OnboardCustomerRequestDto` (EmployeeWriteDtos.cs:139-154).
 *
 * THE BODY IS NESTED, and flattening it is the failure mode this comment exists to prevent: a flat
 * body binds BOTH objects to their defaults, so a form that plainly had a legal name comes back
 * 422 "Legal name is required." with nothing in it naming the real fault.
 *
 * Exported under a FIXED NAME. slices/customers/ imports this type and `onboardCustomer` from this
 * slice -- GeneralUIArchitecture.md section 1.4 rule C permits importing another slice's `api.ts`
 * and `types.ts` and forbids importing its `queries.ts`. The SCREEN is Customers'
 * (`/customers/new`); the wire types and the `can()` row are this slice's. Do not "tidy" the route
 * into the Customers slice in either direction: EmployeesEndpoints.cs:214-224 marks it LOCKED,
 * because this slice owns two of the three steps and therefore the transaction that makes all three
 * atomic.
 */
export interface OnboardCustomerRequest {
  customer: OnboardCustomerCustomer;
  firstAdmin: OnboardCustomerFirstAdmin;
}

/**
 * Mirrors `OnboardCustomerResponseDto` (EmployeeWriteDtos.cs:168-173) -- all three ids, and NO
 * TOKEN. The invitation token goes to the invitee's mailbox and nowhere else
 * (EmployeesEndpoints.cs:111-112). If a token ever appears in a response, stop and flag it: there
 * is nothing to put in a URL, a log or an analytics call.
 *
 * The route answers 200 via `Results.Ok`, not 201 (EmployeesEndpoints.cs:227).
 */
export interface OnboardCustomerResponse {
  customerId: string;
  employeeId: string;
  userAccountId: string;
}
