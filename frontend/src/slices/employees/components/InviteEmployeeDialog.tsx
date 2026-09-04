import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import Alert from '@mui/material/Alert';
import DialogContentText from '@mui/material/DialogContentText';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { ROLE_LABELS, UserRole } from '../../../shared/format/enums';
import { useInviteEmployee } from '../queries';
import { inviteEmployeeSchema, type InviteEmployeeFormValues } from '../schemas';
import type { EmployeeDetail } from '../types';

/**
 * POST /api/employees/invite -- creates the account and SENDS AN EMAIL. EmployeesScreens.md section
 * 8.6, plan section 9.3.
 *
 * A. THERE IS NO UN-INVITE AND NO DELETE-ACCOUNT ENDPOINT. 02-AuthorizationMatrix.md section 4:
 *    "Delete an Employee record — Nobody." Once invited, the only lever left is *Suspend access*. So
 *    `ConfirmDialog` is MANDATORY and must name the two surprising consequences: an email goes out
 *    IMMEDIATELY to the address shown, and that address becomes the person's PERMANENT LOGIN. This is
 *    the only moment in their working life when that address is chosen (section 5.5 rule B), and this
 *    is the invited-side inverse of the edit dialog's "work email is not a login email" notice.
 *
 * B. THE INVITATION TOKEN NEVER REACHES THE BROWSER. EmployeesEndpoints.cs:111-112: "The token is
 *    never returned in the response — it goes to the invitee's mailbox and nowhere else." There is
 *    nothing to display, nothing to copy and nothing to put in a URL. If a token ever appears in a
 *    response, stop and flag it -- do not render it "just for testing".
 *
 * C. THE ADDRESS IS REQUIRED IN THIS FORM even though `InviteEmployeeRequestDto.LoginEmail` is
 *    nullable. Omitted, the server falls back to the work email on file -- but then the operator has
 *    confirmed "an email goes out to the address shown" with no address shown, which is the one thing
 *    this dialog exists to prevent. It is pre-filled with the work email: the same value the fallback
 *    would have used, now visible and correctable.
 *
 * D. TWO ROLE OPTIONS, AS INTEGERS, HARD-CODED. `CustomerAdmin` is 2 and `Employee` is 3;
 *    EmployeeValidation.cs:110-114 answers 422 "An Employee's role must be CustomerAdmin or Employee."
 *    for either Accountant role. Built from the `UserRole` const and NOT by filtering the four-role
 *    enum, or a future enum member becomes a 422 nobody can explain. A string is a 400 from model
 *    binding, before the handler runs.
 *
 * E. EVERY ERROR IS RENDERED VERBATIM AND THE DIALOG STAYS OPEN. 409 "That email address is already
 *    in use." deliberately DOES NOT SAY WHERE (InviteEmployeeHandler.cs:22) -- it is a system-wide
 *    login-email collision, and embellishing it with "at another Customer" states something the
 *    client does not know and must not imply. Also 409 "This employee already has an account.",
 *    422 "A departed employee cannot be invited." and
 *    422 "No email address on file for this employee." The Actions menu hides the entry for the last
 *    three conditions; these banners are the backstop, not the design.
 *
 * F. NO *RESEND INVITATION*. Whether re-inviting an already-`Invited` person is supported is
 *    unspecified in the backend plan (section 16), so the button is not built and the question is
 *    reported instead of guessed at.
 */
export function InviteEmployeeDialog({
  open,
  employee,
  onClose,
  onInvited,
}: {
  open: boolean;
  /** The loaded detail. Rule C pre-fills from `workEmail`; the id goes in the body. */
  employee: EmployeeDetail;
  onClose: () => void;
  /** The caller owns the snackbar. Receives the display name, for its copy. */
  onInvited: (displayName: string) => void;
}) {
  const invite = useInviteEmployee();
  const displayName = `${employee.givenName} ${employee.familyName}`;

  const form = useForm<InviteEmployeeFormValues>({
    resolver: zodResolver(inviteEmployeeSchema),
    mode: 'onBlur',
    defaultValues: {
      // Rule C.
      loginEmail: employee.workEmail ?? '',
      // The commonest case, and the least privileged of the two. Never defaulted to CustomerAdmin.
      role: UserRole.Employee,
    },
  });

  const close = () => {
    invite.reset();
    form.reset();
    onClose();
  };

  const onSubmit = (values: InviteEmployeeFormValues) => {
    invite.mutate(
      {
        employeeId: employee.id,
        loginEmail: values.loginEmail,
        // Rule D. An integer, from the const object.
        role: values.role,
      },
      {
        onSuccess: () => {
          form.reset();
          onInvited(displayName);
        },
      },
    );
  };

  const loginEmailValue = form.watch('loginEmail');
  const roleValue = form.watch('role');

  return (
    <ConfirmDialog
      open={open}
      title={`Invite ${displayName}?`}
      confirmLabel="Send invitation"
      /* Irreversible, but NOT the destructive direction: section 8.2 reserves red for *Mark
         departed*, the only red button on the screen. */
      confirmColor="primary"
      isPending={invite.isPending}
      onConfirm={() => {
        void form.handleSubmit(onSubmit)();
      }}
      onClose={close}
    >
      <Stack spacing={2}>
        {/* Rule A. The two consequences, named, before the fields. */}
        <DialogContentText>
          An invitation email goes out immediately to the address below, and that address becomes the
          address {employee.givenName} signs in with from then on. There is no way to un-invite
          somebody — the only way back is to suspend their access.
        </DialogContentText>

        <TextField
          {...form.register('loginEmail')}
          label="Login email"
          type="email"
          autoComplete="off"
          fullWidth
          error={form.formState.errors.loginEmail !== undefined}
          helperText={
            form.formState.errors.loginEmail?.message ??
            'This becomes their permanent sign-in address, and replaces the work email on file.'
          }
        />

        {/* Rule D. */}
        <Controller
          name="role"
          control={form.control}
          render={({ field }) => (
            <TextField
              select
              label="Role"
              value={String(field.value)}
              onChange={(event) => {
                // An INTEGER on the wire. `event.target.value` is a string, so it is converted here
                // rather than left for JSON to send `"3"` and the server to answer 400.
                field.onChange(Number(event.target.value));
              }}
              onBlur={field.onBlur}
              inputRef={field.ref}
              fullWidth
              error={form.formState.errors.role !== undefined}
              helperText={
                form.formState.errors.role?.message ??
                'A Customer Admin can manage their company’s employees and tickets.'
              }
            >
              <MenuItem value={String(UserRole.CustomerAdmin)}>
                {ROLE_LABELS[UserRole.CustomerAdmin]}
              </MenuItem>
              <MenuItem value={String(UserRole.Employee)}>
                {ROLE_LABELS[UserRole.Employee]}
              </MenuItem>
            </TextField>
          )}
        />

        {/* The confirmation restated with the actual values in it, because the operator is
            confirming an email send and a permanent login in one click. */}
        <Alert severity="info">
          {loginEmailValue.trim().length === 0
            ? 'Enter the address the invitation should go to.'
            : `${displayName} will be emailed at ${loginEmailValue.trim()} and will sign in as ${ROLE_LABELS[roleValue]}.`}
        </Alert>

        {/* Rule E. Verbatim, dialog left open. */}
        <ErrorBanner error={invite.error} />
      </Stack>
    </ConfirmDialog>
  );
}
