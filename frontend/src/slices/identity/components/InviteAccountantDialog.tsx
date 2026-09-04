import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { UserRole } from '../../../shared/format/enums';
import { useInviteAccountant } from '../queries';
import {
  inviteAccountantSchema,
  type InviteAccountantFormValues,
} from '../screens/inviteAccountantSchema';

/**
 * POST /api/accountants/invite -- three fields, Cancel, Invite, and an ErrorBanner inside the dialog.
 *
 * A DIALOG, NOT A ROUTE, for three reasons in order of weight: GeneralUIArchitecture.md section 4.1's
 * route table has no /accountants/new row and that table is normative; the form fetches nothing, so
 * there is no state worth a bookmarkable URL; and the 409 duplicate is best read with the list still on
 * screen, because the answer to it is usually "she is already there, three rows up".
 *
 * A. `mode: 'onBlur'` (section 9.3 rule A). Submit is disabled ONLY while the mutation is pending,
 *    never because the form is invalid (rule B): a disabled button with no explanation is a dead end,
 *    so submit runs, RHF shows the errors and focus moves to the first one.
 * B. INPUT SURVIVES FAILURE (rule D). Nothing here resets on error -- a 409 keeps every field filled in,
 *    which is what makes "she is already there" a correction rather than a re-entry. The form is reset
 *    on success, and on a deliberate Cancel, and at no other time.
 * C. THE 409 AND EVERY 422 ARE FORM-LEVEL, NEVER MAPPED ONTO A FIELD (section 7.3, and
 *    BACKEND_CHANGES_REQUIRED item 5 -- ProblemDetails carries no field map). A red outline on a guessed
 *    control is worse than none.
 * D. NO "IS THIS ADDRESS TAKEN" PRE-CHECK. No endpoint exists, and one would be the enumeration oracle
 *    the whole auth flow is built to prevent. The 409 does disclose the normalised address, deliberately
 *    -- InviteAccountantHandler.cs:79-82: the caller can already list every account -- and that does not
 *    license a check on an unauthenticated path.
 * E. NO TOKEN AND NO "COPY INVITATION LINK". InviteAccountantHandler.cs:134-142 puts the raw token in
 *    the notification's EmailBody only: not in the 201 body, not in the Location header, not in the
 *    in-app notification. A URL built from the account id would carry no token, fail on
 *    /accept-invitation with a 400, and look like a broken invitation system. The invitee completes the
 *    invitation on /accept-invitation, which TokenLinks.cs builds and mails -- that route is contract,
 *    and its host comes from App__BaseUrl, which if misconfigured breaks every invitation with nothing
 *    in the UI able to detect it (flagged, not checked for).
 */
export function InviteAccountantDialog({
  open,
  onClose,
  onInvited,
}: {
  open: boolean;
  onClose: () => void;
  /** The screen owns the Snackbar and names the address the operator typed. */
  onInvited: (email: string) => void;
}) {
  const invite = useInviteAccountant();

  const form = useForm<InviteAccountantFormValues>({
    resolver: zodResolver(inviteAccountantSchema),
    mode: 'onBlur',
    // AccountantUser as the default: the less privileged of the two, and the common case. Promotion is
    // one row action away and is reversible; an accidental Accountant Admin is not, from this form.
    defaultValues: { email: '', displayName: '', role: UserRole.AccountantUser },
  });

  /** Cancel, backdrop and Escape all land here. Discarding a draft is not "resetting on error". */
  const close = () => {
    invite.reset();
    form.reset();
    onClose();
  };

  const onSubmit = (values: InviteAccountantFormValues) => {
    invite.mutate(
      {
        // Already trimmed by the schema, and NOT lowercased: the server keeps LoginEmail as typed.
        email: values.email,
        displayName: values.displayName,
        // A JSON NUMBER. {"role":"1"} is a 400 from model binding before InviteAccountantHandler runs,
        // so the banner would name no field at all.
        role: values.role,
      },
      {
        // 201 is success -- http.ts branches on response.ok, so there is no non-200 2xx to special-case,
        // and the Location header names the LIST rather than the new row and is never followed. The
        // list invalidation is in the mutation hook; the Snackbar is the screen's.
        onSuccess: () => {
          form.reset();
          onInvited(values.email);
        },
      },
    );
  };

  return (
    <Dialog
      open={open}
      onClose={invite.isPending ? undefined : close}
      maxWidth="sm"
      fullWidth
      aria-labelledby="invite-accountant-title"
    >
      <DialogTitle id="invite-accountant-title">Invite an Accountant</DialogTitle>

      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <DialogContent>
          <Stack spacing={2}>
            <TextField
              {...form.register('email')}
              label="Email address"
              type="email"
              autoComplete="off"
              autoFocus
              error={form.formState.errors.email !== undefined}
              helperText={
                form.formState.errors.email?.message ??
                'The invitation is sent here. It is valid for seven days.'
              }
            />

            <TextField
              {...form.register('displayName')}
              label="Display name"
              autoComplete="off"
              error={form.formState.errors.displayName !== undefined}
              helperText={
                form.formState.errors.displayName?.message ??
                'Shown throughout the app. Only the person themselves can change it, when they accept the invitation.'
              }
            />

            {/*
              TWO OPTIONS AND ONLY TWO, and the value is converted to a NUMBER in onChange: MUI's
              Select hands back whatever the MenuItem carried, and a string reaches the API as
              {"role":"1"}, which is a bare model-binding 400 (section 10.1).
            */}
            <Controller
              name="role"
              control={form.control}
              render={({ field }) => (
                <TextField
                  select
                  label="Role"
                  value={String(field.value)}
                  onChange={(event) => {
                    field.onChange(Number(event.target.value));
                  }}
                  onBlur={field.onBlur}
                  inputRef={field.ref}
                  helperText="An Accountant Admin can invite, suspend, promote and demote Accountants, create Customers and read the audit log."
                >
                  <MenuItem value={String(UserRole.AccountantAdmin)}>Accountant Admin</MenuItem>
                  <MenuItem value={String(UserRole.AccountantUser)}>Accountant User</MenuItem>
                </TextField>
              )}
            />

            {/*
              Rule C: the 409 "An account already exists for '<normalised email>'." and every 422 --
              including the role rejection this form's two options make unreachable -- render here,
              above the submit button, from `title`. ErrorBanner owns the whole taxonomy so this screen
              does not branch on a status code itself.
            */}
            <ErrorBanner error={invite.error} />
          </Stack>
        </DialogContent>

        <DialogActions>
          <Button onClick={close} disabled={invite.isPending}>
            Cancel
          </Button>
          {/* Rule A: pending only. Never `disabled={!form.formState.isValid}`. */}
          <Button type="submit" variant="contained" loading={invite.isPending}>
            Send invitation
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
