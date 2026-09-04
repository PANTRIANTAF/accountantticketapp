import { useEffect, useState } from 'react';
import { Link as RouterLink, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as z from 'zod';
import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { AuthLayout } from '../components/AuthLayout';
import { useAcceptInvitation } from '../queries';
import {
  DISPLAY_NAME_MAX_LENGTH,
  PASSWORD_LENGTH_MESSAGE,
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  PASSWORD_TOO_LONG_MESSAGE,
} from '../types';

/**
 * Route /accept-invitation?token=..., public, no shell. LoginArchitecture.md section 5.1.
 *
 * Structurally identical to ResetPasswordScreen -- token from the query string, read once and dropped
 * from the URL (rule G); one opaque 400 for every failure ("That invitation is invalid or has
 * expired.", AcceptInvitationHandler.cs:17); no session on success, so redirect to /login. Three
 * differences:
 *
 * A. THE TOKEN LIVES SEVEN DAYS, not one hour (Core/UserAccountToken.cs:35). An invitation waits on a
 *    human; a reset answers something the person just did. The copy says seven days for that reason.
 * B. displayName IS OPTIONAL AND ABSENT MEANS "KEEP WHAT THE INVITER TYPED". An empty or
 *    whitespace-only string is treated as absent, NOT as an instruction to blank the name. Capped at
 *    200 -- AcceptInvitationHandler.cs:20, DisplayNameMaximumLength, 422 at :84-86 -- which DIFFERS
 *    from the 255 used for most display names elsewhere in the API. The field is sent ONLY when the
 *    user typed something; see the submit handler.
 * C. THE ACCOUNT MUST STILL BE Invited. A replayed link, or one invited-activated-suspended-
 *    reactivated, gets the same opaque 400. THERE IS NO "RESEND" BUTTON: no anonymous resend endpoint
 *    exists and the person cannot log in to ask for one, so a button here would be a dead end that
 *    looks like a fix. The correct path is a fresh invitation from an Accountant Admin.
 *
 * REDEEMING THE TOKEN *IS* THE EMAIL CONFIRMATION. A separate confirm-your-email step would ask the
 * person to prove the same thing twice by the same means.
 *
 * THE SCREEN IS ROLE-AGNOSTIC AND CANNOT GUESS WHO IT SERVES. All three producers --
 * /api/accountants/invite, /api/employees/invite and /api/customers/onboard -- land the invitee here
 * with the same token purpose, and the caller is anonymous holding an opaque token. So there is no
 * "welcome, Employee of Acme Ltd" copy to write: the client does not know, and asking the server would
 * turn an opaque token into a lookup oracle.
 */
const schema = z.object({
  newPassword: z
    .string()
    .min(PASSWORD_MIN_LENGTH, PASSWORD_LENGTH_MESSAGE)
    .max(PASSWORD_MAX_LENGTH, PASSWORD_TOO_LONG_MESSAGE),
  // Rule B. Optional, so no min: the field is allowed to stay empty and empty means "leave it alone".
  displayName: z
    .string()
    .max(DISPLAY_NAME_MAX_LENGTH, `Use at most ${String(DISPLAY_NAME_MAX_LENGTH)} characters.`),
});

type FormValues = z.infer<typeof schema>;

/** Rule C and the client-side missing-token check render the SAME sentence. One copy. */
const INVALID_INVITATION_MESSAGE = 'That invitation is invalid or has expired.';

export function AcceptInvitationScreen() {
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const acceptInvitation = useAcceptInvitation();

  // Read once at mount, then drop the query string: the token is a single-use credential and a query
  // string reaches history, the referrer of any outbound link, and every script on the page.
  const [token] = useState(() => searchParams.get('token') ?? '');

  useEffect(() => {
    if (location.search !== '') {
      void navigate(location.pathname, { replace: true });
    }
  }, [location.pathname, location.search, navigate]);

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onBlur',
    defaultValues: { newPassword: '', displayName: '' },
  });

  // A missing token cannot succeed, so it is reported without a round trip and with the same wording
  // the server uses for every other failure.
  if (token === '') {
    return (
      <AuthLayout title="Accept your invitation">
        <Stack spacing={2}>
          <Alert severity="error">{INVALID_INVITATION_MESSAGE}</Alert>
          {/* Rule C: no resend affordance. Saying who to ask is the only honest next step. */}
          <Alert severity="info">
            Ask the person who invited you to send a new invitation.
          </Alert>
          <Link component={RouterLink} to="/login" variant="body2">
            Go to sign in
          </Link>
        </Stack>
      </AuthLayout>
    );
  }

  const onSubmit = (values: FormValues) => {
    const typedName = values.displayName.trim();

    acceptInvitation.mutate(
      {
        token,
        newPassword: values.newPassword,
        // Rule B, and GeneralUIArchitecture.md section 9.3 rule F: the KEY IS OMITTED when the user
        // typed nothing. Not `''`. AcceptInvitationHandler happens to treat blank as absent, so
        // sending `''` breaks nothing TODAY -- which is precisely why the habit survives to a field
        // where an empty string means "clear this value".
        ...(typedName === '' ? {} : { displayName: typedName }),
      },
      {
        onSuccess: () => {
          void navigate('/login', {
            replace: true,
            state: { notice: 'Your account is ready. Sign in with your new password.' },
          });
        },
      },
    );
  };

  return (
    <AuthLayout
      title="Accept your invitation"
      subtitle="Choose a password to finish setting up your account. Invitation links are valid for seven days."
    >
      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <Stack spacing={2}>
          <TextField
            {...form.register('newPassword')}
            label="Password"
            type="password"
            autoComplete="new-password"
            autoFocus
            error={form.formState.errors.newPassword !== undefined}
            helperText={
              form.formState.errors.newPassword?.message ??
              `At least ${String(PASSWORD_MIN_LENGTH)} characters. No other requirements.`
            }
          />
          <TextField
            {...form.register('displayName')}
            label="Your name (optional)"
            autoComplete="name"
            error={form.formState.errors.displayName !== undefined}
            helperText={
              form.formState.errors.displayName?.message ??
              'Leave this empty to keep the name you were invited under.'
            }
          />

          {/* One opaque 400 for all of rule C's causes, rendered verbatim. */}
          <ErrorBanner error={acceptInvitation.error} />

          <Button
            type="submit"
            variant="contained"
            size="large"
            loading={acceptInvitation.isPending}
          >
            Set password and continue
          </Button>
        </Stack>
      </form>
    </AuthLayout>
  );
}
