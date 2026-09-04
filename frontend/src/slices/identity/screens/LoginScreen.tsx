import { useEffect, useState } from 'react';
import { Link as RouterLink, useLocation, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as z from 'zod';
import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { resolvePostLoginPath } from '../../../shared/auth/RequireSession';
import { useSessionExpiry } from '../../../shared/auth/useSession';
import { AuthLayout } from '../components/AuthLayout';
import { useLogin } from '../queries';

/**
 * Route /login, public, no shell. LoginArchitecture.md sections 2.1-2.5.
 *
 * TWO FIELDS AND A SUBMIT BUTTON. THE PASSWORD POLICY DOES NOT APPLY HERE (section 2.1): the policy
 * governs CHOOSING a password, and a client-side min(12) on this form locks a user whose existing
 * password predates the rule out of their own account, with a validation message that makes no sense
 * to them. `min(1)` and nothing more.
 *
 * SEVEN THINGS THIS SCREEN MUST NOT DO (section 2.5), all absent below:
 *
 *   1. Check whether an email exists before submitting -- that is the enumeration oracle the opaque
 *      401 exists to prevent.
 *   2. Offer a role picker. The role is a property of the account, not a login choice.
 *   3. Offer "remember me". Expiry is 8 hours sliding and fixed at IdentityRegistration.cs:86-87, so
 *      the checkbox would do nothing at all.
 *   4. Count attempts or disable the form after N failures. Lockout is 5 failures then 15 minutes
 *      (LoginHandler.cs:28,30) and the response never says so; the client does not know whether the
 *      account exists, let alone its counter. Nor does the form rate-limit itself: the handler
 *      deliberately does not extend a lockout on repeated attempts, because that turns brute-force
 *      protection into a denial of service against the victim.
 *   5. Show a password strength meter -- there is nothing being chosen here.
 *   6. Pre-fill the email from localStorage. Nothing is stored client-side at all (section 0.2).
 *   7. Link to a register page. THERE IS NONE: accounts come only from /api/accountants/invite,
 *      /api/employees/invite or /api/customers/onboard.
 */

/**
 * What the router's location state may carry into this screen.
 *
 * `from` is set by RequireSession when it bounces an anonymous visitor off a protected route, and is
 * validated by resolvePostLoginPath -- never trusted as a path. `notice` is set by
 * ResetPasswordScreen and AcceptInvitationScreen, which finish without a session and hand the user
 * here with a sentence to read.
 *
 * The path is in LOCATION STATE, NOT IN A ?returnTo= PARAMETER (section 2.3 rule A): a query parameter
 * is an open redirect the moment it is allowed to hold an absolute URL, and sanitising it correctly is
 * more work than never having it.
 */
export interface LoginRedirectState {
  from?: unknown;
  notice?: string;
}

const schema = z.object({
  email: z.string().min(1, 'Enter your email address.').email('Enter a valid email address.'),
  password: z.string().min(1, 'Enter your password.'),
});

type FormValues = z.infer<typeof schema>;

export function LoginScreen() {
  const location = useLocation();
  const navigate = useNavigate();
  const login = useLogin();
  const { expired, clearExpired } = useSessionExpiry();

  const state = readState(location.state);

  // Read the intended path ONCE, at mount, and never again (section 2.3 rule D: "a path left in state
  // redirects the next login too, which surfaces weeks later as 'logging in sends me to a random
  // page'"). Navigating away with `replace` and no state is what actually clears it.
  const [from] = useState<unknown>(() => state.from);
  const [notice] = useState<string | undefined>(() => state.notice);

  // The session-expiry message is shown ONCE, HERE, and then the flag is dropped (section 7 rule B: a
  // toast on the page they were leaving unmounts with that page). Copying it into local state before
  // clearing means a later visit to /login does not repeat it.
  const [sessionEnded, setSessionEnded] = useState(false);
  useEffect(() => {
    if (expired) {
      setSessionEnded(true);
      clearExpired();
    }
  }, [expired, clearExpired]);

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onBlur',
    defaultValues: { email: '', password: '' },
  });

  const onSubmit = (values: FormValues) => {
    login.mutate(values, {
      onSuccess: (session) => {
        // CHECK mustChangePassword BEFORE ROUTING ANYWHERE (section 2.1). Every other route 403s
        // while the flag is set, so routing by role first produces an immediate bounce the user sees
        // as a flicker, and a stored `from` path they never reach.
        if (session.mustChangePassword) {
          void navigate('/change-password', { replace: true });
          return;
        }

        // resolvePostLoginPath applies rules B and C: only a path starting with a single `/`, and only
        // one this role may actually see -- otherwise the role's own landing route. Without rule C a
        // CustomerAdmin bounced off /audit logs in successfully and is shown access-denied as the first
        // thing they see.
        void navigate(resolvePostLoginPath(from, session.role), { replace: true });
      },
    });
  };

  return (
    <AuthLayout title="Sign in">
      {sessionEnded && (
        <Alert severity="info" sx={{ mb: 2 }}>
          Your session has ended. Sign in again.
        </Alert>
      )}
      {notice !== undefined && (
        <Alert severity="success" sx={{ mb: 2 }}>
          {notice}
        </Alert>
      )}

      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <Stack spacing={2}>
          <TextField
            {...form.register('email')}
            label="Email"
            type="email"
            autoComplete="username"
            autoFocus
            error={form.formState.errors.email !== undefined}
            helperText={form.formState.errors.email?.message}
          />
          <TextField
            {...form.register('password')}
            label="Password"
            type="password"
            autoComplete="current-password"
            error={form.formState.errors.password !== undefined}
            helperText={form.formState.errors.password?.message}
          />

          {/*
            ONE 401 WITH ONE MESSAGE FOR SIX CAUSES -- no such account, wrong password, still Invited,
            Suspended, locked out, owning Customer suspended -- and ErrorBanner renders the server's
            `title` verbatim (rule A). Do not append "your account may be suspended" or anything else
            that varies by cause or by attempt: every embellishment is a channel that answers "does
            this address have an account here".

            A 422 here is a malformed request rather than a credential failure (rule D) and indicates a
            client bug; a 429 comes from Caddy, carries no account information, and is the one status
            this form is allowed to be specific about (rule E). ErrorBanner already distinguishes both.

            The banner sits ABOVE the submit button, inside the form, and nothing the user typed is
            cleared (section 7.2).
          */}
          <ErrorBanner error={login.error} />

          <Button type="submit" variant="contained" size="large" loading={login.isPending}>
            Sign in
          </Button>

          <Link component={RouterLink} to="/forgot-password" variant="body2">
            Forgot your password?
          </Link>
        </Stack>
      </form>
    </AuthLayout>
  );
}

function readState(state: unknown): LoginRedirectState {
  if (typeof state !== 'object' || state === null) return {};
  const candidate = state as { from?: unknown; notice?: unknown };
  return {
    from: candidate.from,
    ...(typeof candidate.notice === 'string' ? { notice: candidate.notice } : {}),
  };
}
