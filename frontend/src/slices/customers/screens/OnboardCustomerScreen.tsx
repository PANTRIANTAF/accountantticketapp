import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { useForm } from 'react-hook-form';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { zodResolver } from '@hookform/resolvers/zod';
import * as z from 'zod';
import Button from '@mui/material/Button';
import Card from '@mui/material/Card';
import CardContent from '@mui/material/CardContent';
import CardHeader from '@mui/material/CardHeader';
import Divider from '@mui/material/Divider';
import Link from '@mui/material/Link';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { ApiError } from '../../../shared/api/ApiError';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { PageHeader } from '../../../shared/components/PageHeader';
import { onboardCustomer } from '../../employees/api';
import type {
  OnboardCustomerRequest,
  OnboardCustomerResponse,
} from '../../employees/types';
import { customerKeys } from '../queries';
import { customerBlockSchema, firstAdminBlockSchema } from '../schemas';

/**
 * Route /customers/new, AccountantAdmin ONLY (GeneralUIArchitecture.md section 4.1;
 * EmployeesActionCatalogue.cs:22 grants "OnboardCustomer" to AA alone).
 *
 * THE ONE FILE IN THIS SLICE THAT IMPORTS ANOTHER SLICE, AND THE IMPORT IS THE POINT.
 * POST /api/customers/onboard is registered by EmployeesEndpoints.cs:227, which marks it LOCKED,
 * because the Employees slice owns two of the three steps and therefore the transaction that makes all
 * three atomic (03-SliceInventory.md section 1). api.ts mirrors the endpoint FILE, so the wrapper and
 * its two types live in slices/employees/ and this screen imports them -- section 1.4 rule C permits
 * exactly `api.ts` and `types.ts` of another slice, and this is its second legitimate use in the
 * application. NOTHING ELSE from slices/employees/ may be imported here: not a screen, not a
 * component, and above all not its queries.ts.
 *
 * A SECOND WRAPPER IN slices/customers/api.ts WOULD BE THE DEFECT THIS AVOIDS -- two functions
 * posting one body to one route, drifting apart on the first DTO change.
 *
 * ONE REQUEST, AND A WIZARD IS BANNED (CustomersScreens.md section 5.2). Splitting the submit across
 * /api/customers/create and then /api/employees/register + /invite puts a network boundary inside an
 * operation that is atomic on purpose. A 422 on the work email, a 409 on the login address, or a
 * closed laptop between the two calls then leaves a Customer row with no Employee and no account:
 * nobody can sign in, no screen can finish the job, and there is NO resume-onboarding endpoint. A
 * purely visual stepper would be acceptable; a stepper whose *Next* issues a request is the banned
 * design. Twenty fields in two headed sections on one page is enough.
 *
 * NO ROLE SELECT. OnboardCustomerHandler.cs:104-114 chooses CustomerAdmin itself and `role` is not a
 * request field. Creating the first person as a plain Employee would put the Customer in violation of
 * its own at-least-one-active-Customer-Admin invariant from the moment it exists, and the set-role
 * guard would then block every attempt to climb out.
 *
 * NO "COPY INVITATION LINK". The response is three ids and NO TOKEN
 * (OnboardCustomerHandler.cs:153-160): the invitation is emailed to the invitee and reaches the SPA
 * nowhere. If a token ever appears in that response, stop and flag it.
 */

/**
 * ONE SCHEMA, ONE zodResolver PASS, BOTH BLOCKS AT ONCE -- and that is a deliberate improvement on
 * the server's order rather than a copy of it. OnboardCustomerHandler.cs:61-68 validates `firstAdmin`
 * BEFORE delegating the Customer half at :74, so a request wrong in both places returns only the
 * first-admin 422; a client that validated sequentially would reproduce that misery exactly -- fix the
 * work email, submit, receive a second 422 about the legal name.
 *
 * COMPOSED HERE, NOT IN schemas.ts. The nesting is a property of this one request body
 * (EmployeeWriteDtos.cs:139-154) and not of either block, and schemas.ts exports four schemas.
 *
 * THE BODY IS NESTED AND FLATTENING IT IS THE CLASSIC FAILURE: a flat body binds BOTH objects to
 * their defaults, and a form that plainly had a legal name comes back 422 "Legal name is required."
 * with nothing in it naming the real fault.
 */
const onboardSchema = z.object({
  customer: customerBlockSchema,
  firstAdmin: firstAdminBlockSchema,
});

type OnboardInput = z.input<typeof onboardSchema>;
type OnboardOutput = z.output<typeof onboardSchema>;

/**
 * Today in UTC as a DateOnly string, for the two date defaults.
 *
 * UTC because the server's ceilings are computed from DateTime.UtcNow (CustomerValidation.cs:17,
 * EmployeeValidation.cs:143). Seeding a LOCAL date east of UTC would prefill tomorrow, which for
 * onboardedOn -- ceiling +1 day -- is legal but wrong, and would silently record the wrong day.
 */
const todayUtc = (): string => new Date().toISOString().slice(0, 10);

export function OnboardCustomerScreen() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  /**
   * THE MUTATION LIVES HERE, NOT IN queries.ts, and it is the one exception in the slice to
   * "screens call hooks, never api.ts" (section 3.2 rule A). queries.ts owns the Customers slice's
   * seven Customers-endpoint hooks; this write is another slice's endpoint, imported under rule C, so
   * putting a hook for it in customers/queries.ts would claim ownership of a route this slice does not
   * register.
   *
   * IT CANNOT SEED THE DETAIL KEY -- the one write in the slice that cannot. The response is three
   * ids, not a CustomerDto (section 5.3), so the detail screen fetches for itself after the navigate.
   * Assembling a Customer from the form values is banned twice over: section 3.2 rule E forbids
   * optimistic writes outright, and here the guess is concretely wrong, because CustomerValidation
   * trimmed and normalised every string before it was stored.
   *
   * `retry: false` is inherited from Phase 0's queryClient and is load-bearing: /onboard is not
   * idempotent, and a retry after a timeout would create a second Customer, a second Employee and a
   * second invitation for one click.
   */
  const mutation = useMutation<OnboardCustomerResponse, Error, OnboardCustomerRequest>({
    mutationFn: onboardCustomer,
    onSuccess: (created) => {
      void queryClient.invalidateQueries({ queryKey: customerKeys.lists });
      void navigate(`/customers/${created.customerId}`);
    },
  });

  const form = useForm<OnboardInput, unknown, OnboardOutput>({
    resolver: zodResolver(onboardSchema),
    mode: 'onBlur',
    // Every field starts as '' rather than undefined, so each input is controlled from the first
    // render and each optional reaches the resolver as a string it can transform to null.
    defaultValues: {
      customer: {
        legalName: '',
        tradingName: '',
        taxNumber: '',
        taxOffice: '',
        addressLine1: '',
        addressLine2: '',
        addressCity: '',
        addressPostalCode: '',
        addressCountry: '',
        contactEmail: '',
        contactPhone: '',
        onboardedOn: todayUtc(),
      },
      firstAdmin: {
        givenName: '',
        familyName: '',
        jobTitle: '',
        workEmail: '',
        contactPhone: '',
        taxIdentificationNumber: '',
        socialSecurityNumber: '',
        employmentStartDate: todayUtc(),
      },
    },
  });

  /**
   * The parsed output IS the request body: both halves are already trimmed, and every untouched
   * optional is already null rather than '' (schemas.ts rule C). No assembly, no `|| null`, no second
   * shape to keep in step with the DTO.
   */
  const onSubmit = (values: OnboardOutput) => {
    mutation.mutate(values);
  };

  const errors = form.formState.errors;

  return (
    <Stack spacing={3}>
      <PageHeader title="Add Customer" />

      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <Stack spacing={3}>
          {/*
            THE UPPER BLOCK IS A COMPANY, AND THE HEADING IS WHAT STOPS A READER CARRYING
            PERSON-SHAPED LABELS UPWARD (section 12.1 rule B). "Legal name" and "Trading name" --
            never "First name", never "Company name", because the entity IS the Customer, and never
            "Client", which 00-Glossary.md bans outright.
          */}
          <Card variant="outlined">
            <CardHeader title="Company" subheader="The Customer" />
            <Divider />
            <CardContent>
              <Stack spacing={2}>
                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  <TextField
                    {...form.register('customer.legalName')}
                    label="Legal name"
                    required
                    autoFocus
                    fullWidth
                    error={errors.customer?.legalName !== undefined}
                    helperText={errors.customer?.legalName?.message}
                  />
                  <TextField
                    {...form.register('customer.tradingName')}
                    label="Trading name"
                    fullWidth
                    error={errors.customer?.tradingName !== undefined}
                    helperText={errors.customer?.tradingName?.message ?? 'Optional.'}
                  />
                </Stack>

                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  {/* Uniqueness is a 409 from the database, not a shape this form can check
                      (CreateCustomerHandler; OnboardCustomerHandler.cs:74). */}
                  <TextField
                    {...form.register('customer.taxNumber')}
                    label="Tax number"
                    required
                    fullWidth
                    error={errors.customer?.taxNumber !== undefined}
                    helperText={
                      errors.customer?.taxNumber?.message ?? 'Must be unique across all Customers.'
                    }
                  />
                  <TextField
                    {...form.register('customer.taxOffice')}
                    label="Tax office"
                    fullWidth
                    error={errors.customer?.taxOffice !== undefined}
                    helperText={errors.customer?.taxOffice?.message ?? 'Optional.'}
                  />
                </Stack>

                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  <TextField
                    {...form.register('customer.addressLine1')}
                    label="Address line 1"
                    required
                    fullWidth
                    error={errors.customer?.addressLine1 !== undefined}
                    helperText={errors.customer?.addressLine1?.message}
                  />
                  <TextField
                    {...form.register('customer.addressLine2')}
                    label="Address line 2"
                    fullWidth
                    error={errors.customer?.addressLine2 !== undefined}
                    helperText={errors.customer?.addressLine2?.message ?? 'Optional.'}
                  />
                </Stack>

                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  <TextField
                    {...form.register('customer.addressCity')}
                    label="City"
                    required
                    fullWidth
                    error={errors.customer?.addressCity !== undefined}
                    helperText={errors.customer?.addressCity?.message}
                  />
                  <TextField
                    {...form.register('customer.addressPostalCode')}
                    label="Postal code"
                    required
                    fullWidth
                    error={errors.customer?.addressPostalCode !== undefined}
                    helperText={errors.customer?.addressPostalCode?.message}
                  />
                  <TextField
                    {...form.register('customer.addressCountry')}
                    label="Country"
                    required
                    fullWidth
                    error={errors.customer?.addressCountry !== undefined}
                    helperText={errors.customer?.addressCountry?.message}
                  />
                </Stack>

                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  {/* The company's switchboard and inbox, not a person's -- the person's are below. */}
                  <TextField
                    {...form.register('customer.contactEmail')}
                    label="Contact email"
                    type="email"
                    required
                    fullWidth
                    error={errors.customer?.contactEmail !== undefined}
                    helperText={errors.customer?.contactEmail?.message}
                  />
                  <TextField
                    {...form.register('customer.contactPhone')}
                    label="Contact phone"
                    required
                    fullWidth
                    error={errors.customer?.contactPhone !== undefined}
                    helperText={errors.customer?.contactPhone?.message}
                  />
                </Stack>

                {/*
                  A DateOnly, kept a STRING END TO END (GeneralUIArchitecture.md section 10.2). A
                  native date input reads and writes "YYYY-MM-DD", which is exactly the wire format,
                  so no Date is ever constructed and no local timezone can shift the day. The ceiling
                  is +1 day (CustomerValidation.cs:17), mirrored in the schema as a string comparison
                  and stated in the helper text so the user is not surprised by it.
                  shrink is set because a date input always has a visible value.
                */}
                <TextField
                  {...form.register('customer.onboardedOn')}
                  label="Onboarded on"
                  type="date"
                  required
                  slotProps={{ inputLabel: { shrink: true } }}
                  sx={{ maxWidth: 240 }}
                  error={errors.customer?.onboardedOn !== undefined}
                  helperText={
                    errors.customer?.onboardedOn?.message ??
                    'At most one day in the future. Cannot be changed afterwards.'
                  }
                />
              </Stack>
            </CardContent>
          </Card>

          {/*
            THE LOWER BLOCK IS A NATURAL PERSON, AND THIS IS THE ONLY PLACE IN THE SLICE WHERE
            "Given name" AND "Family name" ARE CORRECT (section 12.1 rule C) -- those fields belong to
            OnboardFirstAdminDto, an EMPLOYEE, a different entity in a different slice. The heading
            writes "Customer Admin" in full: "Admin" alone is ambiguous between two roles with
            different powers (00-Glossary.md).
          */}
          <Card variant="outlined">
            <CardHeader title="First Customer Admin" subheader="An Employee — a person" />
            <Divider />
            <CardContent>
              <Stack spacing={2}>
                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  <TextField
                    {...form.register('firstAdmin.givenName')}
                    label="Given name"
                    required
                    fullWidth
                    error={errors.firstAdmin?.givenName !== undefined}
                    helperText={errors.firstAdmin?.givenName?.message}
                  />
                  <TextField
                    {...form.register('firstAdmin.familyName')}
                    label="Family name"
                    required
                    fullWidth
                    error={errors.firstAdmin?.familyName !== undefined}
                    helperText={errors.firstAdmin?.familyName?.message}
                  />
                </Stack>

                <TextField
                  {...form.register('firstAdmin.jobTitle')}
                  label="Job title"
                  error={errors.firstAdmin?.jobTitle !== undefined}
                  helperText={errors.firstAdmin?.jobTitle?.message ?? 'Optional.'}
                />

                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  {/*
                    REQUIRED HERE, unlike plain employee registration (EmployeeValidation.cs:94-96),
                    because this operation always invites and the invitation needs somewhere to go.
                    The helper text says what the address becomes, since the invited person's login
                    identity is decided by this field and by nothing else on the form.
                  */}
                  <TextField
                    {...form.register('firstAdmin.workEmail')}
                    label="Work email"
                    type="email"
                    required
                    fullWidth
                    error={errors.firstAdmin?.workEmail !== undefined}
                    helperText={
                      errors.firstAdmin?.workEmail?.message ??
                      'The invitation is sent here and this becomes their sign-in address.'
                    }
                  />
                  <TextField
                    {...form.register('firstAdmin.contactPhone')}
                    label="Contact phone"
                    fullWidth
                    error={errors.firstAdmin?.contactPhone !== undefined}
                    helperText={errors.firstAdmin?.contactPhone?.message ?? 'Optional.'}
                  />
                </Stack>

                <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                  <TextField
                    {...form.register('firstAdmin.taxIdentificationNumber')}
                    label="Tax identification number"
                    fullWidth
                    error={errors.firstAdmin?.taxIdentificationNumber !== undefined}
                    helperText={
                      errors.firstAdmin?.taxIdentificationNumber?.message ?? 'Optional.'
                    }
                  />
                  <TextField
                    {...form.register('firstAdmin.socialSecurityNumber')}
                    label="Social security number"
                    fullWidth
                    error={errors.firstAdmin?.socialSecurityNumber !== undefined}
                    helperText={errors.firstAdmin?.socialSecurityNumber?.message ?? 'Optional.'}
                  />
                </Stack>

                {/*
                  +1 YEAR, NOT +1 DAY -- a different ceiling from onboardedOn above
                  (EmployeeValidation.cs:26, 143), because this one is a typo guard against a mistyped
                  year rather than a rule about when a Customer may be recorded.
                */}
                <TextField
                  {...form.register('firstAdmin.employmentStartDate')}
                  label="Employment start date"
                  type="date"
                  required
                  slotProps={{ inputLabel: { shrink: true } }}
                  sx={{ maxWidth: 240 }}
                  error={errors.firstAdmin?.employmentStartDate !== undefined}
                  helperText={
                    errors.firstAdmin?.employmentStartDate?.message ??
                    'At most one year in the future.'
                  }
                />
              </Stack>
            </CardContent>
          </Card>

          {/*
            ONE FORM-LEVEL BANNER, IMMEDIATELY ABOVE THE SUBMIT BUTTON, AND NOTHING IS ATTACHED TO A
            FIELD (section 7.3). ProblemDetails here carries no errors{} dictionary
            (AppExceptionMiddleware.cs:53-58), so there is nothing to attach: a 422 or a 409 is one
            sentence, rendered verbatim, with every typed value left intact.
          */}
          <ErrorBanner error={mutation.error} />

          {/*
            THE 409 FOOTNOTE, BRANCHED ON THE STATUS CODE AND NEVER ON THE MESSAGE. There are two 409s
            -- "A customer with this tax number already exists." (the Customer exists) and "That email
            address is already in use." (the login address is taken, possibly at another Customer) --
            and neither carries an error code, so the UI CANNOT tell them apart programmatically
            (section 5.6). This sentence therefore covers both and identifies neither.

            IT DOES NOT NAME THE OTHER CUSTOMER, AND COULD NOT. OnboardCustomerHandler.cs:116-121
            rewrites Identity's 409 precisely so the response does not reveal which Customer holds the
            address; the client does not know it and must not "improve" the message by guessing.

            "Nothing was created" is the part the operator needs: the whole operation is one
            transaction, so a failure at any step left no Customer, no Employee and no invitation
            behind, and the form can simply be corrected and resubmitted.
          */}
          {mutation.error instanceof ApiError && mutation.error.status === 409 && (
            <Typography variant="body2" color="text.secondary">
              Nothing was created. If this Customer may already exist,{' '}
              <Link component={RouterLink} to="/customers" underline="hover">
                search the Customers list
              </Link>
              ; a sign-in address may also already be in use elsewhere.
            </Typography>
          )}

          <Stack direction="row" spacing={2} sx={{ justifyContent: 'flex-end' }}>
            <Button component={RouterLink} to="/customers">
              Cancel
            </Button>
            {/* Disabled only while pending (section 9.3) -- never on "not dirty" or "has errors": a
                Save the user cannot press, for a reason they cannot see, is unexplainable. */}
            <Button type="submit" variant="contained" loading={mutation.isPending}>
              Create Customer
            </Button>
          </Stack>
        </Stack>
      </form>
    </Stack>
  );
}
