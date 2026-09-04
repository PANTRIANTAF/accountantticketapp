import { useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as z from 'zod';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { AuthLayout } from '../components/AuthLayout';
import { useChangeOwnPassword, useLogout } from '../queries';
import {
  PASSWORD_LENGTH_MESSAGE,
  PASSWORD_MAX_LENGTH,
  PASSWORD_MIN_LENGTH,
  PASSWORD_TOO_LONG_MESSAGE,
} from '../types';

/**
 * Route /change-password, authenticated, NO SHELL. LoginArchitecture.md section 3.
 *
 * THE GATE THAT BREAKS EVERY OTHER SCREEN IF MISSED. An authenticated user whose must_change_password
 * claim is "true" gets 403 ON EVERY ROUTE except exactly three -- /api/auth/change-password,
 * /api/auth/logout and /api/auth/me (Shared/Auth/MustChangePasswordMiddleware.cs).
 *
 * A. ROUTED ON, NOT TOASTED. It is a state the account is in, not a failed action; RequireSession
 *    navigates here and http.ts's 403 handler invalidates the session so RequireSession sees the flag.
 * B. BOTH DETECTION PATHS MUST EXIST. The bootstrap returns the flag, so the app can route here
 *    BEFORE making a request that would 403; the interceptor covers the flag being set by another
 *    session mid-flight. Either alone leaves a hole.
 * C. NO SHELL. Navigation while every destination 403s is a menu of dead links.
 * D. NO "SKIP FOR NOW" -- there is nothing to skip to -- AND LOGOUT MUST BE PRESENT. The middleware
 *    allows /api/auth/logout for exactly this reason, and omitting the button is what makes the gate
 *    feel broken: the user's only remaining escape is clearing cookies by hand.
 *
 * WHO ARRIVES HERE: the seeded first Accountant Admin, because Shared/Seeding/DatabaseSeeder.cs:93
 * sets MustChangePassword = true -- the seeded password came from an environment variable visible in
 * `docker inspect`, in shell history and in the compose file. Nobody else today: AcceptInvitationHandler
 * and CompletePasswordResetHandler both set the flag to false, because the person chose the password
 * themselves.
 */

/**
 * The policy, mirrored from PasswordPolicy.cs and ChangeOwnPasswordHandler.cs. See types.ts for the
 * full table and for why the fifth rule is not in PasswordPolicy.
 *
 * THE DIFFER-FROM-CURRENT RULE BELONGS TO THIS FORM AND TO NO OTHER. Reset and invitation acceptance
 * have no current password to compare against -- the user is proving control of a mailbox, not of an
 * old secret -- and mirroring it there would reject someone who legitimately reuses the password they
 * could not remember well enough to sign in with, with no server rule behind the rejection.
 *
 * THE NOT-EQUAL-LOGIN-EMAIL RULE CANNOT BE CHECKED HERE AT ALL. SessionDto carries no loginEmail, so
 * that comparison is server-side only and arrives as a 422 banner (BACKEND_CHANGES_REQUIRED item 11).
 *
 * AND THERE ARE NO COMPOSITION RULES, DELIBERATELY -- see types.ts. Do not add them.
 */
const schema = z
  .object({
    currentPassword: z.string().min(1, 'Enter your current password.'),
    newPassword: z
      .string()
      .min(PASSWORD_MIN_LENGTH, PASSWORD_LENGTH_MESSAGE)
      .max(PASSWORD_MAX_LENGTH, PASSWORD_TOO_LONG_MESSAGE),
  })
  .refine((values) => values.newPassword !== values.currentPassword, {
    error: 'Choose a password different from your current one.',
    path: ['newPassword'],
  });

type FormValues = z.infer<typeof schema>;

export function ChangePasswordScreen() {
  const navigate = useNavigate();
  const session = useAuthenticatedSession();
  const changePassword = useChangeOwnPassword();
  const logout = useLogout();

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onBlur',
    defaultValues: { currentPassword: '', newPassword: '' },
  });

  const onSubmit = (values: FormValues) => {
    changePassword.mutate(values, {
      // The mutation has already awaited the session refetch (see queries.ts), so by the time this
      // runs the cached flag is false and RequireSession will let the shell render. `/` rather than a
      // role's route: `/` IS the role redirect, so this screen never has to know the table.
      //
      // The user is NOT returned to the deep link they originally asked for: RequireSession sent them
      // here with `replace` and no state, so there is nothing to return to. That is deliberate --
      // carrying an intended path through a password change would mean holding it across a
      // credential change, and the landing route is a defensible place to arrive.
      onSuccess: () => {
        void navigate('/', { replace: true });
      },
    });
  };

  return (
    <AuthLayout
      title="Change your password"
      subtitle={
        session.mustChangePassword
          ? 'Your password must be changed before you can continue.'
          : undefined
      }
    >
      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <Stack spacing={2}>
          <TextField
            {...form.register('currentPassword')}
            label="Current password"
            type="password"
            autoComplete="current-password"
            autoFocus
            error={form.formState.errors.currentPassword !== undefined}
            helperText={form.formState.errors.currentPassword?.message}
          />
          <TextField
            {...form.register('newPassword')}
            label="New password"
            type="password"
            autoComplete="new-password"
            error={form.formState.errors.newPassword !== undefined}
            helperText={
              form.formState.errors.newPassword?.message ??
              `At least ${String(PASSWORD_MIN_LENGTH)} characters. No other requirements.`
            }
          />

          {/*
            TWO ORDERING FACTS ARRIVE HERE AS SERVER ERRORS, AND BOTH ARE RENDERED, NOT ACTED ON.

            The handler validates the NEW password BEFORE verifying the current one
            (ChangeOwnPasswordHandler.cs:65, deliberately), so a 6-character new password comes back as
            a 422 about the new password rather than as a 401 the user reads as "I got my old password
            wrong".

            A WRONG CURRENT PASSWORD IS 401, NOT 403 (:88) -- a failed credential check, which does not
            increment the lockout counter and cannot lock the account, so the copy does not warn that
            it might. That 401 must NOT log the user out; see the conflict note in shared/api/http.ts.

            Every 422 here can only ever be a FORM-LEVEL banner, because ProblemDetails carries no field
            map (GeneralUIArchitecture.md section 7.3) -- including the one rule the client cannot
            pre-check, not-equal-to-your-login-email.
          */}
          <ErrorBanner error={changePassword.error} />

          <Button
            type="submit"
            variant="contained"
            size="large"
            loading={changePassword.isPending}
          >
            Change password
          </Button>

          {/*
            RULE D. Logout is the ONE other action the middleware permits in this state, so the button
            is here rather than in a shell this screen does not render. Logging out twice is a 200 both
            times, so it is not guarded against a double click.
          */}
          <Stack spacing={1} sx={{ alignItems: 'flex-start' }}>
            <Typography variant="body2" color="text.secondary">
              Signed in as {session.displayName}.
            </Typography>
            <Button
              variant="text"
              onClick={() => {
                logout.mutate();
              }}
              loading={logout.isPending}
            >
              Sign out
            </Button>
          </Stack>
        </Stack>
      </form>
    </AuthLayout>
  );
}
