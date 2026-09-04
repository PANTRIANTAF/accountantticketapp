import { Link as RouterLink } from 'react-router-dom';
import Button from '@mui/material/Button';
import Divider from '@mui/material/Divider';
import Link from '@mui/material/Link';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { PageHeader } from '../../../shared/components/PageHeader';
import { ROLE_LABELS } from '../../../shared/format/enums';
import { useLogout } from '../queries';

/**
 * /profile -- reachable by ALL FOUR ROLES, and it ISSUES NO REQUEST OF ITS OWN.
 *
 * Everything on it comes from ['identity','session'], already cached by GET /api/auth/me
 * (IdentityScreens.md section 7). There is no GET /api/auth/profile and nothing here would be different
 * from the session if there were.
 *
 * WHAT IS DELIBERATELY ABSENT, AND WHY -- this screen is mostly defined by it:
 *
 * A. NO DISPLAY-NAME FORM. Across all thirteen routes, the only things any of them writes about the
 *    CALLER are the password and -- on /api/auth/accept-invitation only, once, before first sign-in -- an
 *    optional displayName (AuthDtos.cs:54-58). That is the sole write path for a name and it is
 *    unreachable from an authenticated session. An Accountant Admin cannot rename anybody either:
 *    AccountIdRequestDto has one field. A "Save profile" button would have NOTHING TO POST TO, so the
 *    name is read-only text with a sentence saying who to ask. Punch-list items 10 and 11.
 * B. NO LOGIN-EMAIL FIELD, editable or otherwise. SessionDto carries no email at all (AuthDtos.cs:20-25)
 *    -- "it is not an account-detail response" -- so there is not even a value to prefill.
 *    POST /api/employees/change-login-email is not a counter-example: it takes an employeeId, is granted
 *    to the two Accountant roles only, and lives on the Employee detail screen. Nobody changes their own,
 *    and no Accountant's login email can be changed at all.
 * C. NO PASSWORD FORM, just a LINK. LoginArchitecture.md section 3 owns /change-password, and two forms
 *    posting to one endpoint means two sets of validation drifting apart.
 * D. NOTHING ABOUT ANOTHER PERSON, and above all no "reset this person's password" affordance anywhere
 *    in this slice -- 02-AuthorizationMatrix.md section 11 answers that with "Nobody", which includes
 *    Accountant Admin, and ChangePasswordRequestDto accepts no target user so there is no request shape
 *    such a button could send.
 * E. NO RAW GUID. SessionDto.customerId is null for both Accountant roles and set for CustomerAdmin and
 *    Employee; for a Customer-side caller it becomes a LINK to /my-customer, never printed.
 * F. NO CONTACT-DETAILS REGION. EmployeesScreens.md section 7 specifies that region, on its own screen,
 *    and specifies it read-only because POST /api/employees/update-own-contact is a full replacement that
 *    an un-prefillable form would use to wipe the user's own phone and work email on a 200.
 */
export function ProfileScreen() {
  const session = useAuthenticatedSession();
  const logout = useLogout();

  return (
    <>
      {/* The shared header, like every other screen: it is what focuses the h1 on a route change, so a
          hand-rolled <h1> here would quietly opt this route out of the accessibility floor's rule 3. */}
      <PageHeader title="Your profile" subtitle="Your account details, as the app sees them." />

      <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack spacing={3}>
            <div>
              <Typography variant="overline" color="text.secondary" component="h2">
                Name
              </Typography>
              {/* Rule A. Read-only TEXT, not a disabled TextField: a greyed-out input invites the user
                  to look for the button that enables it. */}
              <Typography variant="body1">{session.displayName}</Typography>
              <Typography variant="body2" color="text.secondary">
                To change this, ask an Accountant Admin.
              </Typography>
            </div>

            <div>
              <Typography variant="overline" color="text.secondary" component="h2">
                Role
              </Typography>
              {/* Through ROLE_LABELS: the glossary label, never the wire number and never the bare word
                  "Admin", which 00-Glossary.md bans because it is ambiguous between AccountantAdmin and
                  CustomerAdmin. */}
              <Typography variant="body1">{ROLE_LABELS[session.role]}</Typography>
            </div>

            {/* Rule E. Rendered only when there is a Customer to link to. */}
            {session.customerId !== null && (
              <div>
                <Typography variant="overline" color="text.secondary" component="h2">
                  Organisation
                </Typography>
                <Link component={RouterLink} to="/my-customer">
                  View your organisation
                </Link>
              </div>
            )}

            <Divider />

            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              {/* Rule C. A link, not a form. */}
              <Button component={RouterLink} to="/change-password" variant="outlined">
                Change password
              </Button>

              {/*
                The SAME mutation the account menu uses (section 5.1), so both paths clear the whole
                query cache and redirect identically. Two logout implementations is how one of them ends
                up leaving the previous user's customer list in memory on a shared office machine.
                Logging out twice is a 200 both times, so a double click needs no guard.
              */}
              <Button
                variant="text"
                color="inherit"
                loading={logout.isPending}
                onClick={() => {
                  logout.mutate();
                }}
              >
                Sign out
              </Button>
          </Stack>
        </Stack>
      </Paper>
    </>
  );
}
