import type { Path, UseFormReturn } from 'react-hook-form';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { startDateMax, type EmployeeFieldsValues } from '../schemas';

/**
 * THE EIGHT FIELDS the register and edit dialogs share, in one component, so the two forms cannot
 * drift into two different field lists. Created in the phase that first needs it (registration) so
 * the edit dialog composes rather than copies.
 *
 * `customerId` is NOT here: it exists on registration only, is a picker rather than a text field, and
 * is hidden entirely for a Customer Admin (EmployeesScreens.md section 6.2). `employmentEndDate` is
 * not here either -- only `depart` and `reinstate` move it, never an edit.
 *
 * A. THE WORK-EMAIL NOTICE IS REQUIRED COPY AND IS PASSED IN, not chosen here. Section 5.5 rule A
 *    branches it by role: an Accountant is pointed at *Change login email* in the Actions menu, and a
 *    Customer Admin is told that only the accounting office can change a login email. Telling a
 *    Customer Admin to use an action they are refused is the same dead end in a new place. The three
 *    wordings are the exported constants below.
 *
 * B. THE START DATE IS A NATIVE DATE INPUT, whose value is already "YYYY-MM-DD" -- a C# `DateOnly`
 *    with no timezone. Nothing here builds a `Date` from it: `new Date("2024-03-01")` parses as
 *    midnight UTC and prints as the previous day anywhere west of it
 *    (GeneralUIArchitecture.md section 10.2). `max` mirrors the server's one-year ceiling so the
 *    picker cannot offer a value the schema will refuse.
 *
 * C. NO REQUIRED MARKERS BEYOND THE SCHEMA'S. Zod owns the rules; a `required` attribute on the input
 *    would let the browser's own validation message pre-empt the server's wording, which is what
 *    `noValidate` on the form exists to prevent.
 */

/** Section 5.5 rule A, the Accountant branch. Verbatim. */
export const WORK_EMAIL_NOTICE_ACCOUNTANT =
  'Work email is contact information. It is not the address this person signs in with. To change how they log in, use Change login email in the Actions menu.';

/** Section 5.5 rule A, the Customer Admin and Employee branch. Verbatim. */
export const WORK_EMAIL_NOTICE_CUSTOMER_SIDE =
  'Work email is contact information. It is not the address this person signs in with, and changing it here does not change how they log in. Only the accounting office can change a login email — contact them.';

/**
 * Registration only. There is no account yet, so neither branch of rule A is true: registering
 * creates no login and sends no email (EmployeesEndpoints.cs:29), and the address becomes a login
 * only if somebody later invites this person.
 */
export const WORK_EMAIL_NOTICE_REGISTER =
  'Optional. Contact information only — registering creates no account and sends no email. If this person is invited later, this is the address the invitation goes to.';

type FieldName = keyof EmployeeFieldsValues;

export function EmployeeFieldset<T extends EmployeeFieldsValues>({
  form,
  workEmailNotice,
  autoFocusFirstField = false,
}: {
  form: UseFormReturn<T>;
  /** Rule A. One of the three constants above; never composed inline at a call site. */
  workEmailNotice: string;
  autoFocusFirstField?: boolean;
}) {
  /**
   * The two casts in this component, and the only ones in the slice. `T` is constrained to extend
   * `EmployeeFieldsValues`, so every name below IS a key of `T` -- but TypeScript cannot prove that
   * for an unresolved generic, and the alternative is duplicating eight fields per form, which is
   * exactly the drift this component exists to prevent.
   */
  const bind = (name: FieldName) => form.register(name as Path<T>);
  const errors = form.formState.errors as Partial<Record<FieldName, { message?: string }>>;
  const helper = (name: FieldName, fallback: string): string =>
    errors[name]?.message ?? fallback;
  const invalid = (name: FieldName): boolean => errors[name] !== undefined;

  return (
    <Stack spacing={2}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
        <TextField
          {...bind('givenName')}
          label="Given name"
          autoComplete="off"
          autoFocus={autoFocusFirstField}
          fullWidth
          error={invalid('givenName')}
          helperText={helper('givenName', ' ')}
        />
        <TextField
          {...bind('familyName')}
          label="Family name"
          autoComplete="off"
          fullWidth
          error={invalid('familyName')}
          helperText={helper('familyName', 'The list is sorted by family name.')}
        />
      </Stack>

      <TextField
        {...bind('jobTitle')}
        label="Job title"
        autoComplete="off"
        error={invalid('jobTitle')}
        helperText={helper('jobTitle', 'Optional.')}
      />

      {/* Rule A. The notice is present before any field is touched, never revealed on error. */}
      <TextField
        {...bind('workEmail')}
        label="Work email"
        type="email"
        autoComplete="off"
        error={invalid('workEmail')}
        helperText={helper('workEmail', workEmailNotice)}
      />

      <TextField
        {...bind('contactPhone')}
        label="Contact phone"
        autoComplete="off"
        error={invalid('contactPhone')}
        helperText={helper('contactPhone', 'Optional.')}
      />

      {/*
        The two identifying numbers are stored in plain text and are the fields the Office files taxes
        with. They are ordinary inputs here -- the masking on the detail screen is about a screen-share,
        not about a control, and masking them in a form the operator is filling in would only produce
        typos nobody can see (EmployeesScreens.md section 5.4).
      */}
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
        <TextField
          {...bind('taxIdentificationNumber')}
          label="Tax identification number"
          autoComplete="off"
          fullWidth
          error={invalid('taxIdentificationNumber')}
          helperText={helper('taxIdentificationNumber', 'Optional.')}
        />
        <TextField
          {...bind('socialSecurityNumber')}
          label="Social security number"
          autoComplete="off"
          fullWidth
          error={invalid('socialSecurityNumber')}
          helperText={helper('socialSecurityNumber', 'Optional.')}
        />
      </Stack>

      {/* Rule B. */}
      <TextField
        {...bind('employmentStartDate')}
        label="Employment start date"
        type="date"
        slotProps={{ inputLabel: { shrink: true }, htmlInput: { max: startDateMax() } }}
        error={invalid('employmentStartDate')}
        helperText={helper(
          'employmentStartDate',
          'A start date more than a year ahead is refused as a likely typo.',
        )}
      />
    </Stack>
  );
}
