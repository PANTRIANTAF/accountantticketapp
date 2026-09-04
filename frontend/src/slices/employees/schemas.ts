import * as z from 'zod';
import { UserRole } from '../../shared/format/enums';

/**
 * THE ZOD SCHEMAS, mirrored from `Slices/Employees/Application/EmployeeValidation.cs` EXACTLY.
 *
 * A client limit STRICTER than the server's blocks legitimate input and the user cannot discover
 * which of the two rules is imaginary; a LOOSER one produces the unattachable banner of
 * GeneralUIArchitecture.md section 7.3, because ProblemDetails here is `{ status, title, traceId }`
 * with no field map (BACKEND_CHANGES_REQUIRED item 5) -- so a 422 the client could have caught is a
 * CLIENT defect, fixed by adding the rule here.
 *
 *   givenName, familyName          required, trimmed, <= 100    EmployeeValidation.cs:32-33
 *   jobTitle                       optional, <= 200             :34
 *   workEmail                      optional, <= 320, has '@'    :150-156 (OptionalEmail)
 *   contactPhone                   optional, <= 50              :36
 *   taxIdentificationNumber        optional, <= 50              :37-38
 *   socialSecurityNumber           optional, <= 50              :39-40
 *   employmentStartDate            required, <= today + 1 year  :138-148
 *   customerId (register only)     required, non-empty Guid     :30-31
 *   employmentEndDate (depart)     required, >= start date      DepartEmployeeHandler.cs:65-72
 *   loginEmail                     required, <= 320, one '@'    Identity/EmailNormalization.cs:30-52
 *
 * A. CONTAINING '@' IS THE WHOLE WORK-EMAIL RULE ON THE SERVER. `OptionalEmail` does one
 *    `Contains('@')` and nothing else. Do not add a regex.
 *
 * B. THE LOGIN EMAIL IS VALIDATED BY IDENTITY AND ITS RULE IS STRICTER -- exactly one '@' with
 *    something on both sides, then `System.Net.Mail.MailAddress` parses it
 *    (EmailNormalization.Require). Mirrored to the structural check only, for the same reason as
 *    rule A: "deliberately not a regular expression: an over-clever pattern rejects legitimate
 *    addresses, and the invitation email is the real validator." The messages below are the SERVER's
 *    own wording, so a client rejection and a 422 for the same mistake do not read as two rules.
 *
 * C. THE ONE-YEAR START-DATE CEILING IS INVENTED, and says so in the C# source itself
 *    (EmployeeValidation.cs:22-26: "FLAGGED: this threshold is invented"). Mirrored because the
 *    server enforces it; expect it to change. Flagged again in the plan's section 16.
 *
 * D. THE FORM'S VALUES ARE STRINGS; THE REQUEST'S OPTIONAL FIELDS ARE `string | null`. A text input
 *    produces `''`, and `''` is a value: a C# `string?` binding treats `""` and `null` differently,
 *    and `""` can pass a nullability check while failing a format one (section 9.3 rule F). The
 *    schemas therefore trim (rule E) and `nullIfBlank` below does the `'' -> null` conversion at the
 *    one place every call site passes through.
 *
 * E. DATES ARE PLAIN "YYYY-MM-DD" STRINGS AND NEVER BECOME A `Date`. A `DateOnly` has no timezone,
 *    and `new Date("2024-03-01")` parses as midnight UTC and prints as the previous day anywhere west
 *    of it (section 10.2). The forms use a native date input, whose value is already this format, so
 *    nothing here converts between a string and a `Date` at all.
 */

const NAME_MAX_LENGTH = 100;
const JOB_TITLE_MAX_LENGTH = 200;
const EMAIL_MAX_LENGTH = 320;
const SHORT_FIELD_MAX_LENGTH = 50;

/** ListEmployeesHandler.cs:70-72 -- 422 "Search must be at most 200 characters." */
export const SEARCH_TERM_MAX_LENGTH = 200;

/** EmployeeValidation.cs:26 -- `MaximumStartDateYearsAhead`. Invented server-side; see rule C. */
export const MAXIMUM_START_DATE_YEARS_AHEAD = 1;

/** The all-zero Guid. `CustomerId == Guid.Empty` is 422 "Customer is required." */
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;

/**
 * Today plus the ceiling, as "YYYY-MM-DD" -- compared as STRINGS, which is exactly right for this
 * format: ISO dates sort lexicographically, so no `Date` is constructed for the comparison and the
 * day-shift trap of section 10.2 cannot occur. The server computes its ceiling from `DateTime.UtcNow`;
 * a browser east of UTC can therefore be up to a day ahead of it, which WIDENS the client's window
 * rather than narrowing it. That is the safe direction: the server is the authority, and a client
 * stricter than the server rejects input the server would accept.
 */
function startDateCeilingIso(): string {
  const now = new Date();
  const ceiling = new Date(
    now.getFullYear() + MAXIMUM_START_DATE_YEARS_AHEAD,
    now.getMonth(),
    now.getDate(),
  );
  const month = String(ceiling.getMonth() + 1).padStart(2, '0');
  const day = String(ceiling.getDate()).padStart(2, '0');
  return `${String(ceiling.getFullYear())}-${month}-${day}`;
}

/** Rule D. `''` after trimming is an absent optional field, and absent means `null` on the wire. */
export function nullIfBlank(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/** The maximum a native date input should offer for a start date. Exported so the field can set `max`. */
export const startDateMax = startDateCeilingIso;

// ---------------------------------------------------------------------------------------------
// The nine shared fields
// ---------------------------------------------------------------------------------------------

const givenName = z
  .string()
  .trim()
  .min(1, 'Given name is required.')
  .max(NAME_MAX_LENGTH, `Given name must be at most ${String(NAME_MAX_LENGTH)} characters.`);

const familyName = z
  .string()
  .trim()
  .min(1, 'Family name is required.')
  .max(NAME_MAX_LENGTH, `Family name must be at most ${String(NAME_MAX_LENGTH)} characters.`);

const jobTitle = z
  .string()
  .trim()
  .max(JOB_TITLE_MAX_LENGTH, `Job title must be at most ${String(JOB_TITLE_MAX_LENGTH)} characters.`);

/** Optional. Rule A: length, then one `Contains('@')`, and nothing else. */
const workEmail = z
  .string()
  .trim()
  .max(EMAIL_MAX_LENGTH, `Work email must be at most ${String(EMAIL_MAX_LENGTH)} characters.`)
  .refine((value) => value.length === 0 || value.includes('@'), "Work email must contain '@'.");

const contactPhone = z
  .string()
  .trim()
  .max(
    SHORT_FIELD_MAX_LENGTH,
    `Contact phone must be at most ${String(SHORT_FIELD_MAX_LENGTH)} characters.`,
  );

const taxIdentificationNumber = z
  .string()
  .trim()
  .max(
    SHORT_FIELD_MAX_LENGTH,
    `Tax identification number must be at most ${String(SHORT_FIELD_MAX_LENGTH)} characters.`,
  );

const socialSecurityNumber = z
  .string()
  .trim()
  .max(
    SHORT_FIELD_MAX_LENGTH,
    `Social security number must be at most ${String(SHORT_FIELD_MAX_LENGTH)} characters.`,
  );

/** Required, a real date, and at most a year ahead (rule C). `default` in C# is 0001-01-01 and is refused. */
const employmentStartDate = z
  .string()
  .trim()
  .min(1, 'Employment start date is required.')
  .regex(DATE_ONLY, 'Enter the employment start date as a date.')
  .refine(
    (value) => value <= startDateCeilingIso(),
    `Employment start date cannot be more than ${String(MAXIMUM_START_DATE_YEARS_AHEAD)} year(s) in the future.`,
  );

/**
 * The eight fields the register and edit dialogs share -- `EmployeeFieldset`'s contract. `customerId`
 * is register-only and `employeeId` is edit-only, so neither is here.
 */
export const employeeFieldsShape = {
  givenName,
  familyName,
  jobTitle,
  workEmail,
  contactPhone,
  taxIdentificationNumber,
  socialSecurityNumber,
  employmentStartDate,
};

const employeeFieldsSchema = z.object(employeeFieldsShape);

export type EmployeeFieldsValues = z.infer<typeof employeeFieldsSchema>;

// ---------------------------------------------------------------------------------------------
// Register
// ---------------------------------------------------------------------------------------------

/**
 * Register: the eight shared fields plus `customerId`.
 *
 * `workEmail` IS OPTIONAL HERE and required by /api/customers/onboard. Do not copy the onboarding
 * form's validation across (EmployeesScreens.md section 6.2 rule C): a registered Employee with no
 * email is legitimate, and the consequence -- 422 "No email address on file for this employee." --
 * belongs on the Invite dialog.
 */
export const registerEmployeeSchema = z.object({
  ...employeeFieldsShape,
  customerId: z
    .string()
    .trim()
    .min(1, 'Customer is required.')
    .refine((value) => value !== EMPTY_GUID, 'Customer is required.'),
});

export type RegisterEmployeeFormValues = z.infer<typeof registerEmployeeSchema>;

// ---------------------------------------------------------------------------------------------
// Edit
// ---------------------------------------------------------------------------------------------

/**
 * Edit: the eight shared fields, and ONE extra rule that depends on the record being edited.
 *
 * `422 "Employment start date cannot be after the recorded employment end date."`
 * (UpdateEmployeeHandler.cs:60-61) is reachable only on a Departed record, so the rule is added only
 * when the loaded detail has an `employmentEndDate`. Mirroring it means it never arrives as a banner
 * that names a field the form did not know was involved (plan section 9.2 rule C).
 *
 * A factory, not a constant, because the ceiling is a property of the loaded row rather than of the
 * form. The end date itself is NOT editable here -- only `depart` and `reinstate` move it.
 */
export function makeEditEmployeeSchema(employmentEndDate: string | null) {
  if (employmentEndDate === null) return employeeFieldsSchema;

  return employeeFieldsSchema.refine(
    (values) => values.employmentStartDate <= employmentEndDate,
    {
      path: ['employmentStartDate'],
      message: 'Employment start date cannot be after the recorded employment end date.',
    },
  );
}

export type EditEmployeeFormValues = EmployeeFieldsValues;

// ---------------------------------------------------------------------------------------------
// Depart
// ---------------------------------------------------------------------------------------------

/**
 * Depart: an end date, required, NOT BEFORE the start date, and with NO UPPER BOUND -- a future date
 * is normal for a notice period (`DepartEmployeeRequestDto`). Both messages are the handler's own:
 * 422 "Employment end date is required." and
 * 422 "Employment end date cannot be before the employment start date." (DepartEmployeeHandler.cs:66,
 * :71-72).
 *
 * A future date does NOT schedule anything: the record flips to Departed on submit either way, which
 * is why the dialog copy says so.
 */
export function makeDepartEmployeeSchema(employmentStartDate: string) {
  return z.object({
    employmentEndDate: z
      .string()
      .trim()
      .min(1, 'Employment end date is required.')
      .regex(DATE_ONLY, 'Enter the employment end date as a date.')
      .refine(
        (value) => value >= employmentStartDate,
        'Employment end date cannot be before the employment start date.',
      ),
  });
}

export interface DepartEmployeeFormValues {
  employmentEndDate: string;
}

// ---------------------------------------------------------------------------------------------
// Invite, change login email, set role
// ---------------------------------------------------------------------------------------------

/** Rule B: Identity's structural check, and nothing more clever than it. */
const loginEmail = z
  .string()
  .trim()
  .min(1, 'An email address is required.')
  .max(EMAIL_MAX_LENGTH, `The email address must be at most ${String(EMAIL_MAX_LENGTH)} characters long.`)
  .refine(
    (value) => value.split('@').length === 2 && !value.startsWith('@') && !value.endsWith('@'),
    'That email address is not valid.',
  );

/**
 * Invite: the address that becomes the person's PERMANENT LOGIN, plus the role the new account gets.
 *
 * TWO ROLE OPTIONS AND ONLY TWO, as NUMBERS. EmployeeValidation.cs:110-114 answers
 * 422 "An Employee's role must be CustomerAdmin or Employee." for either Accountant role. Do not
 * build the select by filtering the four-role enum, or an added enum member becomes a 422 nobody can
 * explain.
 *
 * The address is required in the FORM even though `InviteEmployeeRequestDto.LoginEmail` is nullable:
 * the server falls back to the work email on file, and a blank field would mean the operator confirms
 * "an email goes out to the address shown" without an address being shown. The field is pre-filled
 * from the work email, which is the same value that fallback would have used.
 */
export const inviteEmployeeSchema = z.object({
  loginEmail,
  role: z.union([z.literal(UserRole.CustomerAdmin), z.literal(UserRole.Employee)]),
});

export type InviteEmployeeFormValues = z.infer<typeof inviteEmployeeSchema>;

/**
 * Change login email: one required address, and it is NEVER pre-filled from the work email. They are
 * different addresses that are usually equal, so pre-filling the wrong one turns a change into a
 * silent revert (plan section 12 rule E). `EmployeeDetail` carries no login email, so the field starts
 * empty.
 */
export const changeLoginEmailSchema = z.object({ loginEmail });

export type ChangeLoginEmailFormValues = z.infer<typeof changeLoginEmailSchema>;

/**
 * Set role: the same two options as invite. `SetEmployeeRoleHandler.cs:67-68` answers
 * 422 "This employee already has that role." for a no-op, which is why the option matching the
 * target's current role is DISABLED in the select rather than left to a round trip.
 */
export const setEmployeeRoleSchema = z.object({
  role: z.union([z.literal(UserRole.CustomerAdmin), z.literal(UserRole.Employee)]),
});

export type SetEmployeeRoleFormValues = z.infer<typeof setEmployeeRoleSchema>;

