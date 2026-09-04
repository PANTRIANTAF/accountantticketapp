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
import { useCompletePasswordReset } from '../queries';
import {
  PASSWORD_LENGTH_MESSAGE,
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  PASSWORD_TOO_LONG_MESSAGE,
} from '../types';

/**
 * Route /reset-password?token=..., public, no shell. LoginArchitecture.md section 4.2.
 *
 * A. EVERY FAILURE IS ONE 400 WITH ONE MESSAGE -- "That link is invalid or has expired."
 *    (CompletePasswordResetHandler.cs:16) -- covering no such token, wrong purpose, already consumed,
 *    expired, and the account being suspended between the request and the click. ErrorBanner renders
 *    the server's `title` verbatim. DO NOT try to distinguish expiry from consumption: the server will
 *    not tell you, on purpose, and a client that guesses tells the holder of a stolen link which of
 *    the five it was.
 * B. A MISSING OR EMPTY token IS A 400 TOO, detected here with no round trip and rendered with the
 *    same sentence rather than submitted as an empty string.
 * C. COMPLETING A RESET DOES NOT SIGN THE USER IN. The handler's comment is explicit: a leaked reset
 *    link must not grant a live session in one step. So this screen redirects to /login with a
 *    message, and DOES NOT call /api/auth/me hoping for a session -- there is none.
 * D. THE RESET ALSO CLEARS THE LOCKOUT. Worth knowing when a user reports "I reset my password and
 *    still cannot get in": that symptom is not this flow.
 * E. THE TOKEN MUST NEVER REACH HISTORY, A LOG OR AN ANALYTICS CALL (GeneralUIArchitecture.md
 *    section 9.3 rule G). It is read ONCE into component state and the URL is REPLACED, which is what
 *    the useState initialiser and the effect below are for. It is a single-use credential in a query
 *    parameter -- already the weakest link in the flow -- and the browser's history, the referrer of
 *    any outbound link, and every third-party script on the page can all read a query string.
 *
 * The password schema is section 4.3's, MINUS the differ-from-current rule, which cannot apply: the
 * user is not authenticated and there is nothing to compare against. Mirroring it here would reject
 * someone who legitimately reuses the password they could not remember well enough to sign in with,
 * with no server rule behind the rejection.
 */
const schema = z.object({
  newPassword: z
    .string()
    .min(PASSWORD_MIN_LENGTH, PASSWORD_LENGTH_MESSAGE)
    .max(PASSWORD_MAX_LENGTH, PASSWORD_TOO_LONG_MESSAGE),
});

type FormValues = z.infer<typeof schema>;

/** Rule A and rule B render the SAME sentence. One copy, so they cannot drift apart. */
const INVALID_LINK_MESSAGE = 'That link is invalid or has expired.';

export function ResetPasswordScreen() {
  const [searchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const completeReset = useCompletePasswordReset();

  // Rule E, first half: read once, at mount, and hold it in state. Reading it from searchParams on
  // every render would work, but it would also keep the credential in the URL, which is the thing
  // being avoided.
  const [token] = useState(() => searchParams.get('token') ?? '');

  // Rule E, second half: drop the query string from the URL. `replace` so the version WITH the token
  // does not stay in the history stack behind a back button.
  useEffect(() => {
    if (location.search !== '') {
      void navigate(location.pathname, { replace: true });
    }
  }, [location.pathname, location.search, navigate]);

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onBlur',
    defaultValues: { newPassword: '' },
  });

  // Rule B. No request at all: an empty token cannot succeed, and submitting it would put a
  // meaningless attempt in the audit log.
  if (token === '') {
    return (
      <AuthLayout title="Set a new password">
        <Stack spacing={2}>
          <Alert severity="error">{INVALID_LINK_MESSAGE}</Alert>
          <Link component={RouterLink} to="/forgot-password" variant="body2">
            Request a new reset link
          </Link>
        </Stack>
      </AuthLayout>
    );
  }

  const onSubmit = (values: FormValues) => {
    completeReset.mutate(
      { token, newPassword: values.newPassword },
      {
        // Rule C. The user has no session, so there is nothing to seed and nowhere to go but the login
        // form -- with a sentence saying what happened, because arriving at a bare login form after a
        // successful reset reads as a failure.
        onSuccess: () => {
          void navigate('/login', {
            replace: true,
            state: { notice: 'Your password has been changed. Sign in with your new password.' },
          });
        },
      },
    );
  };

  return (
    <AuthLayout
      title="Set a new password"
      subtitle="Choose a new password for your account. You will be asked to sign in with it."
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
            label="New password"
            type="password"
            autoComplete="new-password"
            autoFocus
            error={form.formState.errors.newPassword !== undefined}
            helperText={
              form.formState.errors.newPassword?.message ??
              `At least ${String(PASSWORD_MIN_LENGTH)} characters. No other requirements.`
            }
          />

          {/* Rule A: the server's one opaque 400, verbatim. */}
          <ErrorBanner error={completeReset.error} />

          <Button type="submit" variant="contained" size="large" loading={completeReset.isPending}>
            Set password
          </Button>

          <Link component={RouterLink} to="/login" variant="body2">
            Back to sign in
          </Link>
        </Stack>
      </form>
    </AuthLayout>
  );
}
