import { Link as RouterLink } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as z from 'zod';
import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { AuthLayout } from '../components/AuthLayout';
import { useRequestPasswordReset } from '../queries';

/**
 * Route /forgot-password, public, no shell, reachable from a link on /login.
 * LoginArchitecture.md section 4.1.
 *
 * THIS ENDPOINT RETURNS 200 UNCONDITIONALLY. IdentityEndpoints.cs:42 declares
 * .Produces<MarkedResultDto>() and nothing else -- no 404 and no 422 -- because an unknown address
 * must get the same answer as a known one. The handler does not even validate the format, on the
 * grounds that a 422 for a malformed address and a 200 for a well-formed unknown one is the same
 * oracle, just quieter.
 *
 * A. THE CONFIRMATION IS NEUTRAL, ALWAYS. "If that address has an account, a reset link is on its
 *    way." NOT "check your inbox" -- that implies an account exists -- and not "we could not find that
 *    address", which is impossible: the server never says so, and saying it here would invent an
 *    answer the API deliberately refuses to give.
 * B. THE FORMAT CHECK IS CLIENT-SIDE ONLY, so a typo is caught before the user waits for an email that
 *    will never arrive. It is NOT a security control; the server ignores the format entirely.
 * C. THE FORM IS REPLACED BY THE CONFIRMATION ON SUCCESS. A live form invites repeated submissions and
 *    EACH ONE INVALIDATES THE PREVIOUS TOKEN: a user who clicks twice and then opens the first email
 *    gets "that link is invalid or has expired" for a link that was valid one minute ago.
 * D. THE TOKEN LIVES ONE HOUR (Core/UserAccountToken.cs:36,
 *    PasswordResetLifetime = TimeSpan.FromHours(1)), so the confirmation states the window; the email
 *    says it too.
 */
const schema = z.object({
  email: z.string().min(1, 'Enter your email address.').email('Enter a valid email address.'),
});

type FormValues = z.infer<typeof schema>;

export function ForgotPasswordScreen() {
  const requestReset = useRequestPasswordReset();

  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onBlur',
    defaultValues: { email: '' },
  });

  const onSubmit = (values: FormValues) => {
    requestReset.mutate(values);
  };

  // Rule C. `isSuccess` and not a local flag, so the form cannot come back while the mutation is
  // still settling.
  if (requestReset.isSuccess) {
    return (
      <AuthLayout title="Check your email">
        {/*
          Rule A: the wording commits to NOTHING about whether the address is known. Every alternative
          -- "we have sent you an email", "no account found" -- answers the one question the endpoint's
          unconditional 200 exists to refuse.
        */}
        <Stack spacing={2}>
          <Alert severity="success">
            If that address has an account, a reset link is on its way.
          </Alert>
          <Typography variant="body2" color="text.secondary">
            The link is valid for one hour. If it expires, ask for a new one.
          </Typography>
          <Link component={RouterLink} to="/login" variant="body2">
            Back to sign in
          </Link>
        </Stack>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Forgot your password?"
      subtitle="Enter your email address and we will send you a link to set a new password."
    >
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

          {/*
            There is no documented failure to render here -- the endpoint has exactly one declared
            response -- so this banner only ever shows a transport failure, a 429 from Caddy, or a 5xx.
            It is still present: swallowing those would leave a user pressing a button that appears to
            do nothing.
          */}
          <ErrorBanner error={requestReset.error} />

          <Button type="submit" variant="contained" size="large" loading={requestReset.isPending}>
            Send reset link
          </Button>

          <Link component={RouterLink} to="/login" variant="body2">
            Back to sign in
          </Link>
        </Stack>
      </form>
    </AuthLayout>
  );
}
