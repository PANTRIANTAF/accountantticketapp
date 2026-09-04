import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { UserRole } from '../../../shared/format/enums';
import { employeeKeys, useUpdateEmployee } from '../queries';
import {
  makeEditEmployeeSchema,
  nullIfBlank,
  type EditEmployeeFormValues,
} from '../schemas';
import {
  EmployeeFieldset,
  WORK_EMAIL_NOTICE_ACCOUNTANT,
  WORK_EMAIL_NOTICE_CUSTOMER_SIDE,
} from './EmployeeFieldset';
import type { EmployeeDetail } from '../types';

/**
 * POST /api/employees/update -- A FULL REPLACEMENT, and the most destructive-by-accident call in the
 * slice. EmployeesScreens.md section 5.5, plan section 9.2.
 *
 * A. PRE-FILL EVERYTHING; SUBMIT EVERYTHING. `UpdateEmployeeRequestDto` says it outright: "omitting
 *    WorkEmail clears it." A form that submits only the fields the user touched sends `null` for the
 *    rest and SILENTLY ERASES the tax identification number, the social-security number, the phone and
 *    the work email -- with a 200, no warning and no undo. That is why this component takes a LOADED
 *    `EmployeeDetail` as a prop rather than an id: if the detail has not resolved, the dialog does not
 *    open, because there is nothing safe to submit. For the same reason there is no inline-cell edit
 *    and no bulk edit anywhere in this slice.
 *
 * B. THE WORK EMAIL IS NOT THE LOGIN EMAIL, and the notice says so BEFORE any field is touched -- and
 *    it differs by role (section 5.5 rule A). An Accountant is pointed at *Change login email* in the
 *    Actions menu; a Customer Admin is told that only the accounting office can move a login email and
 *    to contact them. Pointing a Customer Admin at an action they are refused is the same dead end in
 *    a new place. Without this copy a Customer Admin "fixes" a colleague's login here, believes it
 *    done, and the colleague keeps failing to sign in while nobody can find the cause.
 *
 * C. A DEPARTED EMPLOYEE'S RECORD IS STILL EDITABLE, DELIBERATELY. UpdateEmployeeHandler:
 *    "Correcting a misspelled name or a wrong tax number after somebody has left is ordinary work."
 *    Nothing here disables the form on `status === "Departed"`.
 *
 * D. THE 409 IS NOT A LOST-UPDATE WARNING. 409 "An employee with this work email already exists at
 *    this customer." is the per-Customer uniqueness constraint. There is no optimistic concurrency
 *    anywhere in this backend: two Admins editing one Employee both get 200 and the second write wins
 *    silently. `EmployeeDetail` carries no version and no `updatedAt`, and the version-number
 *    mitigation used for ticket types has NO COUNTERPART here -- do not synthesise one from
 *    `createdAt`. The reload affordance below refetches the record, because rule A makes a stale
 *    pre-fill the dangerous state.
 *
 * E. THE END-DATE RULE IS PART OF THE SCHEMA, NOT OF THIS COMPONENT.
 *    422 "Employment start date cannot be after the recorded employment end date." is reachable only
 *    on a Departed record, so `makeEditEmployeeSchema` takes the loaded `employmentEndDate` and adds
 *    the rule only when there is one. The end date itself is NOT editable here: only `depart` and
 *    `reinstate` move it.
 */
export function EditEmployeeDialog({
  open,
  employee,
  role,
  onClose,
  onSaved,
}: {
  open: boolean;
  /** RESOLVED, never partial -- rule A. */
  employee: EmployeeDetail;
  /** The SESSION role, which picks the work-email notice -- rule B. Not the target's role. */
  role: UserRole;
  onClose: () => void;
  onSaved: (updated: EmployeeDetail) => void;
}) {
  const update = useUpdateEmployee();
  const queryClient = useQueryClient();

  const isAccountant = role === UserRole.AccountantAdmin || role === UserRole.AccountantUser;

  const form = useForm<EditEmployeeFormValues>({
    // Rule E. The schema depends on the loaded row, so it is built from it.
    resolver: zodResolver(makeEditEmployeeSchema(employee.employmentEndDate)),
    mode: 'onBlur',
    /**
     * Rule A. EVERY field, pre-filled. `null` becomes `''` for the input and `nullIfBlank` turns it
     * back into `null` on submit -- the round trip is lossless, and a field the user never touches
     * goes back exactly as it arrived.
     */
    defaultValues: {
      givenName: employee.givenName,
      familyName: employee.familyName,
      jobTitle: employee.jobTitle ?? '',
      workEmail: employee.workEmail ?? '',
      contactPhone: employee.contactPhone ?? '',
      taxIdentificationNumber: employee.taxIdentificationNumber ?? '',
      socialSecurityNumber: employee.socialSecurityNumber ?? '',
      employmentStartDate: employee.employmentStartDate,
    },
  });

  const close = () => {
    update.reset();
    form.reset();
    onClose();
  };

  const onSubmit = (values: EditEmployeeFormValues) => {
    update.mutate(
      {
        employeeId: employee.id,
        givenName: values.givenName,
        familyName: values.familyName,
        // Rule A: all eight, every time. Never a partial body.
        jobTitle: nullIfBlank(values.jobTitle),
        workEmail: nullIfBlank(values.workEmail),
        contactPhone: nullIfBlank(values.contactPhone),
        taxIdentificationNumber: nullIfBlank(values.taxIdentificationNumber),
        socialSecurityNumber: nullIfBlank(values.socialSecurityNumber),
        employmentStartDate: values.employmentStartDate,
      },
      {
        onSuccess: (updated) => {
          onSaved(updated);
        },
      },
    );
  };

  return (
    <Dialog
      open={open}
      onClose={update.isPending ? undefined : close}
      maxWidth="sm"
      fullWidth
      aria-labelledby="edit-employee-title"
    >
      <DialogTitle id="edit-employee-title">
        Edit {employee.givenName} {employee.familyName}
      </DialogTitle>

      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <DialogContent>
          <Stack spacing={2}>
            {/* Rule B. */}
            <EmployeeFieldset
              form={form}
              workEmailNotice={
                isAccountant ? WORK_EMAIL_NOTICE_ACCOUNTANT : WORK_EMAIL_NOTICE_CUSTOMER_SIDE
              }
              autoFocusFirstField
            />

            {/*
              Rule D. `onReload` refetches the record rather than the page: after a 409 the safe move
              is a fresh pre-fill, because a stale one plus rule A's full replacement is how a field
              gets erased. The dialog closes so it reopens on resolved data -- reopening is one click
              and a wrong submit has no undo.
            */}
            <ErrorBanner
              error={update.error}
              onReload={() => {
                void queryClient.invalidateQueries({
                  queryKey: employeeKeys.detail(employee.id),
                });
                close();
              }}
            />
          </Stack>
        </DialogContent>

        <DialogActions>
          <Button onClick={close} disabled={update.isPending}>
            Cancel
          </Button>
          {/* Pending only -- never `disabled={!isValid}`, which hides the reason from the user. */}
          <Button type="submit" variant="contained" loading={update.isPending}>
            Save changes
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
