import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import Alert from '@mui/material/Alert';
import AlertTitle from '@mui/material/AlertTitle';
import DialogContentText from '@mui/material/DialogContentText';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { ROLE_LABELS, UserRole } from '../../../shared/format/enums';
import { useSetEmployeeRole } from '../queries';
import { setEmployeeRoleSchema, type SetEmployeeRoleFormValues } from '../schemas';
import type { EmployeeDetail } from '../types';

/**
 * POST /api/employees/set-role. EmployeesScreens.md sections 8.3 and 8.4, plan section 10.
 *
 * A. `role` IS AN INTEGER: `CustomerAdmin` is 2, `Employee` is 3. The declaration order of the enum is
 *    the contract and a string is a 400 from model binding, before the handler runs. Built from the
 *    `UserRole` const, never a hand-typed literal.
 *
 * B. EXACTLY TWO OPTIONS, HARD-CODED. Either Accountant role is
 *    422 "An Employee's role must be CustomerAdmin or Employee." (EmployeeValidation.cs:110-114),
 *    rejected outright by 02-AuthorizationMatrix.md section 4. Do not build the select by filtering the
 *    four-role enum, or an added enum member becomes a 422 nobody can explain. The option matching the
 *    target's CURRENT role is DISABLED, because a no-op is
 *    422 "This employee already has that role." (SetEmployeeRoleHandler.cs:67-68) -- one of the few
 *    places this slice disables rather than hides, since the current role is the answer to "why", and
 *    it is visible in the same control.
 *
 * C. THE CHANGE IS NOT IMMEDIATE, AND THE OPERATOR MUST BE TOLD. Claims are minted at login, so
 *    SetEmployeeRoleHandler warns that "the target's live session keeps the old role for up to 8 hours"
 *    and that "demotion therefore fails UNSAFE — a demoted Customer Admin keeps administrative powers
 *    until their cookie expires." The demotion copy below ends with the only actionable sentence there
 *    is: suspend their access as well if the change must take effect now. Do not promise immediacy and
 *    do not try to close the gap with a poll (section 9 rule G) -- nothing in this slice polls.
 *
 * D. THE AT-LEAST-ONE-ACTIVE-CUSTOMER-ADMIN INVARIANT IS A 422, NOT A 403.
 *    422 "This Customer must always have at least one active Customer Admin."
 *    (EmployeeInvariants.cs:102-103) guards demoting, departing and suspending. NEVER render
 *    "permission denied" for it: the caller HAS the role; the data's state forbids the operation, and a
 *    403 would suggest re-authenticating as somebody more powerful, which would not help. The dialog
 *    stays OPEN, the title is rendered verbatim, and one line of guidance is added -- promote another
 *    Employee to Customer Admin first, then try again -- because for a Customer Admin that is the only
 *    recovery there is.
 *
 * E. NEVER PREDICT THE INVARIANT CLIENT-SIDE. Counting `role === CustomerAdmin` rows on the current
 *    page is wrong three ways: the page is one of many, `EmployeeSummary` has no `accountStatus` so an
 *    active Customer Admin cannot be told from a suspended one, and the guard has an accepted
 *    concurrency window. A button greyed out on a wrong guess is worse than a 422 -- the user cannot
 *    even attempt the operation to learn why.
 *
 * F. SELF-ACTION IS A SEPARATE 422 FROM A SEPARATE GUARD:
 *    422 "You cannot change your own role or account status." (EmployeeInvariants.cs:126). The Actions
 *    menu hides this entry on the caller's own record, but the client cannot reliably identify its own
 *    record -- `SessionDto.userId` is a UserAccount id, not an employee id -- so the banner is the
 *    backstop, by necessity rather than by preference.
 *
 * The Actions menu also hides this entry when `hasAccount === false`:
 * 422 "This employee has no account. Invite them before setting a role." (SetEmployeeRoleHandler.cs:54-56).
 */
export function SetRoleDialog({
  open,
  employee,
  onClose,
  onChanged,
}: {
  open: boolean;
  employee: EmployeeDetail;
  onClose: () => void;
  onChanged: () => void;
}) {
  const setRole = useSetEmployeeRole();
  const displayName = `${employee.givenName} ${employee.familyName}`;

  const form = useForm<SetEmployeeRoleFormValues>({
    resolver: zodResolver(setEmployeeRoleSchema),
    mode: 'onBlur',
    /**
     * Defaulted to the option the target does NOT currently hold, so the disabled option is never the
     * selected one. `employee.role` is a NULLABLE INTEGER and `AccountantAdmin` is `0`, which is
     * falsy -- hence the explicit comparison rather than a truthiness test.
     */
    defaultValues: {
      role:
        employee.role === UserRole.CustomerAdmin ? UserRole.Employee : UserRole.CustomerAdmin,
    },
  });

  const close = () => {
    setRole.reset();
    form.reset();
    onClose();
  };

  const onSubmit = (values: SetEmployeeRoleFormValues) => {
    setRole.mutate(
      { employeeId: employee.id, role: values.role },
      {
        onSuccess: () => {
          form.reset();
          onChanged();
        },
      },
    );
  };

  const chosen = form.watch('role');
  /** Rule C. A demotion is Customer Admin -> Employee, and only that direction carries the warning. */
  const isDemotion = employee.role === UserRole.CustomerAdmin && chosen === UserRole.Employee;

  return (
    <ConfirmDialog
      open={open}
      title={`Change ${displayName}'s role?`}
      confirmLabel="Change role"
      /* A role change is reversible -- change it back -- so it is not the red button (section 8.2). */
      confirmColor="primary"
      isPending={setRole.isPending}
      onConfirm={() => {
        void form.handleSubmit(onSubmit)();
      }}
      onClose={close}
    >
      <Stack spacing={2}>
        <DialogContentText>
          {employee.role === null
            ? 'This account has no role recorded yet.'
            : `${displayName} is currently ${ROLE_LABELS[employee.role]}.`}
        </DialogContentText>

        {/* Rules A and B. */}
        <Controller
          name="role"
          control={form.control}
          render={({ field }) => (
            <TextField
              select
              label="New role"
              value={String(field.value)}
              onChange={(event) => {
                // Rule A: an integer on the wire, converted here rather than sent as `"3"`.
                field.onChange(Number(event.target.value));
              }}
              onBlur={field.onBlur}
              inputRef={field.ref}
              fullWidth
              error={form.formState.errors.role !== undefined}
              helperText={form.formState.errors.role?.message ?? ' '}
            >
              <MenuItem
                value={String(UserRole.CustomerAdmin)}
                // Rule B: the no-op is refused by the server, so it is refused in the control.
                disabled={employee.role === UserRole.CustomerAdmin}
              >
                {ROLE_LABELS[UserRole.CustomerAdmin]}
              </MenuItem>
              <MenuItem
                value={String(UserRole.Employee)}
                disabled={employee.role === UserRole.Employee}
              >
                {ROLE_LABELS[UserRole.Employee]}
              </MenuItem>
            </TextField>
          )}
        />

        {/* Rule C. Required copy on the demotion path, and the last sentence is the actionable one. */}
        {isDemotion && (
          <Alert severity="warning">
            <AlertTitle>This does not take effect immediately</AlertTitle>
            This takes effect the next time {employee.givenName} signs in. If they are signed in now
            they keep Customer Admin powers until their session expires — up to 8 hours. Suspend their
            access as well if the change must be immediate.
          </Alert>
        )}

        {/* Rules D and F: verbatim, dialog left open. */}
        <ErrorBanner error={setRole.error} />

        {/*
          Rule D's one line of guidance. Shown alongside the banner rather than woven into it, because
          the banner renders the SERVER's wording verbatim and this sentence is the client's advice --
          the two must not be mistaken for one string. It names the recovery for the invariant, which is
          the only 422 here a Customer Admin can act on themselves.
        */}
        {setRole.error !== null && (
          <DialogContentText variant="body2">
            If this Customer would be left without an active Customer Admin, promote another Employee
            to Customer Admin first, then try again.
          </DialogContentText>
        )}
      </Stack>
    </ConfirmDialog>
  );
}
