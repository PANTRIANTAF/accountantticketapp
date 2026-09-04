import { Link as RouterLink } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import Alert from '@mui/material/Alert';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Paper from '@mui/material/Paper';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { PageHeader } from '../../../shared/components/PageHeader';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { ROLE_LABELS } from '../../../shared/format/enums';
import { can } from '../../../shared/permissions/can';
import { getOwnCustomer } from '../../customers/api';

/**
 * `/profile` -- my own account, for all four roles. EmployeesScreens.md section 7, plan section 11.
 *
 * IT LIVES IN THIS SLICE because the only API call it will ever make -- `update-own-contact` -- belongs
 * to this slice. The session comes from `shared/auth/useSession`, which anyone may import, so there is
 * no cross-slice seam to justify for the Account region.
 *
 * A. THE CONTACT-DETAILS FORM CANNOT BE BUILT YET, AND BUILDING IT DESTROYS DATA. `SessionDto` is
 *    `(userId, displayName, role, customerId, mustChangePassword)`; `userId` is a **UserAccount** id, and
 *    `POST /api/employees/get` with it answers 404. `ListEmployeesHandler` excludes the `Employee` role
 *    and `/api/customers/own` returns the company -- so a Customer-side caller has NO PATH TO THEIR OWN
 *    `employeeId` and the form cannot be pre-filled. `UpdateOwnContactRequestDto` is a FULL REPLACEMENT
 *    of its two fields, so an unfilled submit sends `{ workEmail: null, contactPhone: null }` and ERASES
 *    BOTH, with a 200 and a cheerful snackbar. That is why this region is read-only, with no fields and
 *    no submit button, until BACKEND_CHANGES_REQUIRED item 12 lands. A form that cannot be pre-filled
 *    must not be offered.
 *
 * B. NO LOGIN-EMAIL AFFORDANCE ON THIS SCREEN, IN ANY ROLE -- an AccountantAdmin's included. Nobody
 *    changes their own sign-in address: there is no endpoint to build one against, and punch-list item 10
 *    records that the Accountant-only `change-login-email` "is not a precedent for adding one"
 *    (section 8.7 rule B).
 *
 * C. THERE IS NO ADMINISTRATIVE PASSWORD RESET IN THIS APPLICATION, and *Change password* is not one:
 *    it is a link to `/change-password`, where the person supplies their CURRENT password. Nothing on
 *    this screen resets anybody else's password, in any role.
 *
 * D. THE ACCOUNTANT ROLES GET NO CONTACT REGION AT ALL -- not a disabled one. `UpdateOwnContact` excludes
 *    them and `EmployeesActionCatalogue.cs`:41 says why: "an Accountant has no Employee record at all, so
 *    a clean 403 here beats a confusing 404 from the handler." `can()` returns `false` and the region is
 *    HIDDEN (section 6.2 rule C).
 *
 * E. THE LOGIN-EMAIL NOTICE IS STATIC COPY, NEVER `response.notice`. `UpdateOwnContactHandler.cs`:30-33
 *    sets `notice` on every successful WRITE and `EmployeeMapper.ToSelfExpression` never sets it on a
 *    read -- so a screen keyed on its presence shows the warning AFTER the mistake. When the form exists,
 *    `response.notice` is surfaced verbatim in the success snackbar as well, because the server's wording
 *    is written for the user.
 *
 * F. THE CUSTOMER NAME IS FOR THE CUSTOMER-SIDE ROLES ONLY, from `/api/customers/own` -- the one
 *    legitimate cross-slice import, and its key is written literally so it shares the Customers slice's
 *    cache entry without importing that slice's `queries.ts`. An Accountant has no own Customer, so the
 *    query does not run rather than running and 403-ing.
 */
export function ProfileScreen() {
  const session = useAuthenticatedSession();

  /** Rule F. `can(role,'ViewOwnCustomer')` is granted to CustomerAdmin and Employee only. */
  const canSeeOwnCustomer = can(session.role, 'ViewOwnCustomer');
  const ownCustomer = useQuery({
    queryKey: ['customers', 'own'],
    queryFn: getOwnCustomer,
    enabled: canSeeOwnCustomer,
  });

  /** Rule D. Hidden, not disabled. */
  const showContactRegion = can(session.role, 'UpdateOwnContact');

  return (
    <Box>
      <PageHeader title="My profile" />

      <Stack spacing={3}>
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Stack spacing={2}>
            <Typography variant="h6" component="h2">
              Account
            </Typography>

            <ProfileField label="Name" value={session.displayName} />
            {/*
              `role` is an INTEGER on the wire and `AccountantAdmin` is `0`. `ROLE_LABELS` is a
              `Record<UserRole, string>` and is indexed directly -- never `role || 'Unknown'`, which would
              relabel an AccountantAdmin.
            */}
            <ProfileField label="Role" value={ROLE_LABELS[session.role]} />
            {canSeeOwnCustomer && (
              <ProfileField
                label="Customer"
                /* A failed lookup suppresses the name; it never blanks this page. */
                value={ownCustomer.data?.legalName ?? '—'}
              />
            )}

            {/* Rule C. A link, not a reset. */}
            <Box>
              <Button component={RouterLink} to="/change-password" variant="outlined">
                Change password
              </Button>
            </Box>
          </Stack>
        </Paper>

        {/* Rules A, B and D. */}
        {showContactRegion && (
          <Paper variant="outlined" sx={{ p: 3 }}>
            <Stack spacing={2}>
              <Typography variant="h6" component="h2">
                My contact details
              </Typography>

              {/*
                Rule A. NO FIELDS AND NO SUBMIT BUTTON. This is not an oversight and not a loading state:
                the record cannot be read, and a two-field full-replacement form with nothing in it erases
                both values on submit.
              */}
              <Typography variant="body2">
                Your work email and phone number are held on your employee record. Ask a Customer Admin at
                your company to correct either of them.
              </Typography>

              {/* Rule E. Static, always shown, never keyed on `response.notice`. */}
              <Alert severity="info">
                Your work email is contact information only. It is not the address you sign in with, and
                changing it would not change how you log in.
              </Alert>

              {/*
                Section 7.4: the absence of the other fields is a RULE, not a missing feature, so it is
                stated. `UpdateOwnContactRequestDto`: "A person cannot promote themselves, cannot backdate
                their employment, and cannot alter the numbers the Office files taxes with."
              */}
              <Typography variant="body2" color="text.secondary">
                Your name, job title, employment dates and identifying numbers are never editable here —
                only a Customer Admin or the accounting office can change those.
              </Typography>
            </Stack>
          </Paper>
        )}

        {/* Rule D, stated once so the empty space below the Account card reads as deliberate. */}
        {!showContactRegion && (
          <Typography variant="body2" color="text.secondary">
            Accountants have no employee record, so there are no contact details to show here.
          </Typography>
        )}
      </Stack>
    </Box>
  );
}

function ProfileField({ label, value }: { label: string; value: string }) {
  return (
    <Box>
      <Typography variant="caption" color="text.secondary" component="div">
        {label}
      </Typography>
      <Typography variant="body1" component="div">
        {value}
      </Typography>
    </Box>
  );
}
