import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import DialogContentText from '@mui/material/DialogContentText';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { useDepartEmployee } from '../queries';
import { makeDepartEmployeeSchema, type DepartEmployeeFormValues } from '../schemas';
import type { EmployeeDetail } from '../types';

/**
 * POST /api/employees/depart. EmployeesScreens.md section 8.1, plan section 10 rules C-F.
 *
 * A. IT DOES TWO THINGS IN ONE TRANSACTION: records the departure AND suspends the account
 *    immediately. That is the consequence the copy names first, because the operator who wanted only
 *    one of the two has no way back that leaves the other alone.
 *
 * B. IT IS REVERSIBLE ONLY AS A CORRECTION, AND THE COPY MUST CARRY THAT DISTINCTION. `reinstate`
 *    exists, so "irreversible" is now wrong -- but the fix is NOT to soften this into "you can always
 *    undo it". Correcting a departure entered against the wrong record is *Reinstate*; somebody who
 *    genuinely left and later came back is REGISTERED AGAIN, as a second record, so the two periods of
 *    employment stay separate. The two are one click apart, THE SERVER CANNOT TELL THEM APART, and the
 *    audit entry records only which one the caller chose -- so this copy is the whole control.
 *
 * C. THE END DATE IS REQUIRED, HAS NO UPPER BOUND, AND SCHEDULES NOTHING. A future date is normal for
 *    a notice period (`DepartEmployeeRequestDto`), but the record flips to Departed ON SUBMIT either
 *    way. The copy says so, because "end date: next month" reads as "this will happen next month" to
 *    every operator who has used any other HR system. Both Zod rules are the handler's own:
 *    422 "Employment end date is required." and
 *    422 "Employment end date cannot be before the employment start date."
 *    (DepartEmployeeHandler.cs:66, :71-72), mirrored against the LOADED start date.
 *
 * D. THIS IS THE ONLY RED BUTTON ON THE SCREEN (section 8.2). *Suspend access* sits in a different
 *    menu group with a default-coloured button, because it revokes access WITHOUT ending employment and
 *    the wrong choice between the two cannot be taken back cleanly.
 *
 * E. 422 "This employee has already departed." IS A STALE ROW, not a failure to report: render the
 *    banner and let the invalidation that follows correct the screen.
 *
 * F. THE AT-LEAST-ONE-ACTIVE-CUSTOMER-ADMIN INVARIANT AND THE SELF-ACTION GUARD BOTH APPLY HERE, both
 *    as 422s, never as 403s: "This Customer must always have at least one active Customer Admin." and
 *    "You cannot change your own role or account status." The dialog stays open and the guidance line
 *    below names the only recovery a Customer Admin has. Never predicted client-side (plan section 10
 *    rule E).
 */
export function DepartEmployeeDialog({
  open,
  employee,
  onClose,
  onDeparted,
}: {
  open: boolean;
  employee: EmployeeDetail;
  onClose: () => void;
  onDeparted: () => void;
}) {
  const depart = useDepartEmployee();
  const displayName = `${employee.givenName} ${employee.familyName}`;

  const form = useForm<DepartEmployeeFormValues>({
    // Rule C. The floor is the loaded record's start date, not today.
    resolver: zodResolver(makeDepartEmployeeSchema(employee.employmentStartDate)),
    mode: 'onBlur',
    /**
     * EMPTY, deliberately. Defaulting to today would let a confirm-without-reading record a date the
     * operator never chose, and this is the one field in the slice whose value is a fact about a person
     * rather than a formality.
     */
    defaultValues: { employmentEndDate: '' },
  });

  const close = () => {
    depart.reset();
    form.reset();
    onClose();
  };

  const onSubmit = (values: DepartEmployeeFormValues) => {
    depart.mutate(
      { employeeId: employee.id, employmentEndDate: values.employmentEndDate },
      {
        onSuccess: () => {
          form.reset();
          onDeparted();
        },
      },
    );
  };

  return (
    <ConfirmDialog
      open={open}
      title={`Mark ${displayName} as departed?`}
      confirmLabel="Mark departed"
      /* Rule D. */
      confirmColor="error"
      isPending={depart.isPending}
      onConfirm={() => {
        void form.handleSubmit(onSubmit)();
      }}
      onClose={close}
    >
      <Stack spacing={2}>
        {/* Rule A. */}
        <DialogContentText>
          This records that {employee.givenName} has left <strong>and</strong> suspends their access
          immediately, in one step.
        </DialogContentText>

        {/* Rule B. The distinction between a correction and a re-hire, in the dialog that creates it. */}
        <DialogContentText>
          If you enter this against the wrong person you can correct it with <em>Reinstate</em>. That is
          for fixing a mistake — if {employee.givenName} genuinely leaves and later returns, register
          them again as a new Employee. Their tickets stay on this record either way.
        </DialogContentText>

        {/* Rule C. */}
        <TextField
          {...form.register('employmentEndDate')}
          label="Employment end date"
          type="date"
          slotProps={{
            inputLabel: { shrink: true },
            // The floor only. There is deliberately NO `max`: a notice period in the future is normal.
            htmlInput: { min: employee.employmentStartDate },
          }}
          error={form.formState.errors.employmentEndDate !== undefined}
          helperText={
            form.formState.errors.employmentEndDate?.message ??
            'Required. It may be in the future, and the record is marked Departed straight away either way.'
          }
        />

        {/* Rules E and F: verbatim, dialog left open. */}
        <ErrorBanner error={depart.error} />

        {depart.error !== null && (
          <DialogContentText variant="body2">
            If this Customer would be left without an active Customer Admin, promote another Employee to
            Customer Admin first, then try again.
          </DialogContentText>
        )}
      </Stack>
    </ConfirmDialog>
  );
}
