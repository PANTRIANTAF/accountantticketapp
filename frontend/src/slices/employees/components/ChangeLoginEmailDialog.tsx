import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import DialogContentText from '@mui/material/DialogContentText';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { useChangeLoginEmail } from '../queries';
import { changeLoginEmailSchema, type ChangeLoginEmailFormValues } from '../schemas';
import type { EmployeeDetail } from '../types';

/**
 * POST /api/employees/change-login-email -- moves the address the person SIGNS IN WITH.
 * EmployeesScreens.md section 8.7, plan section 12 rules D-G.
 *
 * THE SECOND OF THE TWO ENDPOINTS THAT ANSWERED 403 TO EVERY ROLE UNTIL 2026-09-02:
 * `RequireAsync(user, "ChangeEmployeeLoginEmail")` had no entry in `EmployeesActionCatalogue.cs` and
 * `PermissionChecker` is fail-closed. The catalogue now declares it at line 60, for AccountantAdmin and
 * AccountantUser and nobody else -- verified in the working tree before this file was written, because
 * `Slices/Employees/` is untracked and the fix has no commit behind it.
 *
 * A. ACCOUNTANT ROLES ONLY, AND THAT IS THE POINT, NOT AN OVERSIGHT. 02-AuthorizationMatrix.md
 *    section 4 grants it to AA and AU and to nobody else -- not a Customer Admin, not the account's own
 *    owner: "Whoever can move an account to a new address can move it to a mailbox they control; a
 *    Customer Admin doing it to a colleague is account takeover one step removed, and the colleague is
 *    the one who then cannot log in." The gate is `can(role,'ChangeEmployeeLoginEmail')` IN THE MENU,
 *    not in this dialog: a Customer Admin must not see a disabled entry, because a greyed-out
 *    *Change login email* invites a support request for a power that is deliberately withheld. They see
 *    the edit dialog's work-email notice instead, which tells them to contact the office.
 *
 * B. NOBODY CHANGES THEIR OWN, AT ANY PRIVILEGE LEVEL -- an AccountantAdmin's included. No endpoint
 *    exists to build one against, and punch-list item 10 records that the Accountant-only endpoint "is
 *    not a precedent for adding one". So this affordance never appears on /profile, in any role.
 *
 * C. THE FIELD IS NEVER PRE-FILLED FROM THE WORK EMAIL. They are two different addresses that are
 *    usually equal, so pre-filling the wrong one turns a change into a SILENT REVERT: the operator
 *    confirms a dialog showing the address they meant to move away from. `EmployeeDetail` carries no
 *    login email at all (section 2.3), so the field starts empty -- which is also the honest signal that
 *    the current sign-in address is not something this screen knows.
 *
 * D. THE TWO CONSEQUENCES ARE NAMED, AND SO ARE THE TWO NON-CONSEQUENCES. The old address stops
 *    working; the new one is how they sign in from now on. It does NOT change their password and does
 *    NOT change their work email -- ChangeEmployeeLoginEmailHandler.cs:101-104 leaves the Employee row
 *    untouched -- and a live session keeps working until it expires, up to 8 hours.
 *
 * E. THERE IS NO ADMINISTRATIVE PASSWORD RESET ANYWHERE IN THIS APPLICATION, and this dialog is not a
 *    back door to one. It does not offer to send a password email, and the copy does not imply the
 *    person will be prompted to set a new password.
 *
 * F. ERRORS, VERBATIM, DIALOG LEFT OPEN: 422 "This employee has no account, so there is no sign-in
 *    address to change. Invite them first." (:66-68) -- the menu hides the entry when
 *    `hasAccount === false`; 422 "This employee has departed." (:74) -- hidden when
 *    `status === "Departed"`, with *Reinstate* offered as the first step instead; and a 409 on a
 *    duplicate address whose message deliberately does not say which account holds it. Do not embellish
 *    that one -- the client does not know where the address lives and must not imply it.
 *
 * G. INVALIDATE ON SUCCESS EVEN THOUGH NOTHING VISIBLE CHANGES (handled in `useChangeLoginEmail`). The
 *    work email did not move and the detail carries no login email, so a UI that only re-reads
 *    `workEmail` shows nothing happening -- and the operator runs the operation a second time. The
 *    caller's snackbar is what confirms it.
 */
export function ChangeLoginEmailDialog({
  open,
  employee,
  onClose,
  onChanged,
}: {
  open: boolean;
  employee: EmployeeDetail;
  onClose: () => void;
  /** Receives the new address, for the caller's snackbar -- rule G. */
  onChanged: (loginEmail: string) => void;
}) {
  const change = useChangeLoginEmail();
  const displayName = `${employee.givenName} ${employee.familyName}`;

  const form = useForm<ChangeLoginEmailFormValues>({
    resolver: zodResolver(changeLoginEmailSchema),
    mode: 'onBlur',
    // Rule C. Empty, never `employee.workEmail`.
    defaultValues: { loginEmail: '' },
  });

  const close = () => {
    change.reset();
    form.reset();
    onClose();
  };

  const onSubmit = (values: ChangeLoginEmailFormValues) => {
    const loginEmail = values.loginEmail.trim();
    change.mutate(
      { employeeId: employee.id, loginEmail },
      {
        onSuccess: () => {
          form.reset();
          onChanged(loginEmail);
        },
      },
    );
  };

  return (
    <ConfirmDialog
      open={open}
      title={`Change the address ${displayName} signs in with?`}
      confirmLabel="Change login email"
      /* Reversible -- change it back -- so not the red button (section 8.2). */
      confirmColor="primary"
      isPending={change.isPending}
      onConfirm={() => {
        void form.handleSubmit(onSubmit)();
      }}
      onClose={close}
    >
      <Stack spacing={2}>
        {/* Rule C. */}
        <TextField
          {...form.register('loginEmail')}
          label="New login email"
          type="email"
          autoComplete="off"
          autoFocus
          fullWidth
          error={form.formState.errors.loginEmail !== undefined}
          helperText={
            form.formState.errors.loginEmail?.message ??
            'The address they will sign in with. This is not their work email, and it is not pre-filled from it.'
          }
        />

        {/* Rule D, first half. */}
        <DialogContentText>
          {employee.givenName} will sign in with this address from now on. The old address stops working.
        </DialogContentText>

        {/* Rule D, second half, and rule E. */}
        <DialogContentText variant="body2">
          This does <strong>not</strong> change their password, and it does <strong>not</strong> change
          their work email — those are separate. If they are signed in right now, their session keeps
          working until it expires, up to 8 hours.
        </DialogContentText>

        {/* Rule F. */}
        <ErrorBanner error={change.error} />
      </Stack>
    </ConfirmDialog>
  );
}
