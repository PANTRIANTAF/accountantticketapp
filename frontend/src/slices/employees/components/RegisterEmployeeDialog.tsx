import { Controller, useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { useQuery } from '@tanstack/react-query';
import Autocomplete from '@mui/material/Autocomplete';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { UserRole } from '../../../shared/format/enums';
import { listCustomers } from '../../customers/api';
import type { CustomerSummary } from '../../customers/types';
import { useRegisterEmployee } from '../queries';
import {
  nullIfBlank,
  registerEmployeeSchema,
  type RegisterEmployeeFormValues,
} from '../schemas';
import { EmployeeFieldset, WORK_EMAIL_NOTICE_REGISTER } from './EmployeeFieldset';
import type { EmployeeDetail } from '../types';

/**
 * POST /api/employees/register -- a DIALOG, NEVER A ROUTE.
 *
 * GeneralUIArchitecture.md section 4.1's route table is normative and has `/customers/new` but NO
 * `/employees/new`. Inventing the route would put this slice in conflict with its governing document,
 * and the route would then be missing from `routes.tsx` and from the shell's role gating. A dialog
 * needs neither. It is also right on its merits: nine flat fields, no steps, always opened from
 * `/employees` with the Customer context already on screen, and nothing worth deep-linking to
 * (EmployeesScreens.md section 6.1).
 *
 * A. THIS CREATES AN ACCOUNTLESS EMPLOYEE -- no login, no email (EmployeesEndpoints.cs:29). The title
 *    and the submit button say *Register*: never *Add user*, never *Invite*, never *Create account*.
 *    01-DomainModel.md section 2 calls the Employee/UserAccount separation the most important
 *    structural decision in the model; an accountless Employee can still be the Subject of a Ticket
 *    their Customer Admin opens.
 *
 * B. NO "SEND INVITATION" CHECKBOX. 02-AuthorizationMatrix.md section 4: "Registering and inviting are
 *    two separate operations… A Customer Admin may do the first without ever doing the second." Two
 *    endpoints, two permissions, two audit meanings, and NO TRANSACTION SPANNING THEM -- a checkbox
 *    would make the SPA chain two POSTs, so a failed invite leaves a registered Employee behind an
 *    error message that looks like nothing happened. The offer to invite is in the SNACKBAR, after
 *    the first operation has definitely succeeded.
 *
 * C. THE CUSTOMER PICKER IS DRAWN BY A ROLE CHECK, NOT BY `can()`, and only for the Accountant roles.
 *    A CustomerAdmin's `customerId` comes from the session and the control is not rendered at all:
 *    RegisterEmployeeHandler answers 403 "You may only register employees at your own customer." when
 *    they name another Customer, so drawing the control and then not sending it is the same lie in the
 *    other direction (EmployeesScreens.md section 6.2).
 *
 * D. ACTIVE CUSTOMERS ONLY IN THE PICKER. 422 "This customer is not active." /
 *    "Unknown or inactive customer." -- a suspended Customer cannot gain Employees, so offering one
 *    makes that error the normal case rather than the race case (section 6.2 rule E).
 *
 * E. NEVER RETRY A FAILURE. Nothing here is idempotent and there is no idempotency key: a retry
 *    creates a SECOND Employee (section 6.2 rule F). The submit button is disabled only while the
 *    mutation is pending, and the form is never reset on error (section 9.3 rules B and D).
 */
export function RegisterEmployeeDialog({
  open,
  role,
  sessionCustomerId,
  onClose,
  onRegistered,
}: {
  open: boolean;
  /** The session role. Decides whether the Customer picker exists at all -- rule C. */
  role: UserRole;
  /** `null` for both Accountant roles; the Customer for a CustomerAdmin. */
  sessionCustomerId: string | null;
  onClose: () => void;
  /** The screen owns the Snackbar, and offers *Invite* in its action slot -- rule B. */
  onRegistered: (created: EmployeeDetail) => void;
}) {
  const isAccountant = role === UserRole.AccountantAdmin || role === UserRole.AccountantUser;
  const register = useRegisterEmployee();

  /**
   * Rule D. The one legitimate cross-slice call shape: `slices/customers/api.ts`, permitted by
   * section 1.4 rule C, which allows another slice's `api.ts` and `types.ts` and FORBIDS its
   * `queries.ts`. The key is written out literally rather than imported from `customers/queries.ts`
   * for that reason -- and it is written to MATCH that slice's own list key, so the two share one
   * cache entry instead of fetching the same page twice.
   *
   * `pageSize: 50` is the server's clamp (MAX_PAGE_SIZE), not a preference. A tenant with more than
   * fifty Active Customers cannot pick the fifty-first from this control; no endpoint returns an
   * unpaged Customer list, so that is a real limitation and is flagged rather than worked around with
   * a per-keystroke POST.
   */
  const customerFilters = { status: 'Active' as const, search: null, pageNumber: 1, pageSize: 50 };
  const customers = useQuery({
    queryKey: ['customers', 'list', customerFilters],
    queryFn: () => listCustomers(customerFilters),
    enabled: open && isAccountant,
  });

  const form = useForm<RegisterEmployeeFormValues>({
    resolver: zodResolver(registerEmployeeSchema),
    mode: 'onBlur',
    defaultValues: {
      customerId: sessionCustomerId ?? '',
      givenName: '',
      familyName: '',
      jobTitle: '',
      workEmail: '',
      contactPhone: '',
      taxIdentificationNumber: '',
      socialSecurityNumber: '',
      employmentStartDate: '',
    },
  });

  /** Cancel, backdrop and Escape all land here. Discarding a draft is not "resetting on error". */
  const close = () => {
    register.reset();
    form.reset();
    onClose();
  };

  const onSubmit = (values: RegisterEmployeeFormValues) => {
    register.mutate(
      {
        customerId: values.customerId,
        givenName: values.givenName,
        familyName: values.familyName,
        // Trimmed by the schema; `'' -> null` here, because a C# `string?` treats the two differently
        // and `""` can pass a nullability check while failing a format one (section 9.3 rule F).
        jobTitle: nullIfBlank(values.jobTitle),
        workEmail: nullIfBlank(values.workEmail),
        contactPhone: nullIfBlank(values.contactPhone),
        taxIdentificationNumber: nullIfBlank(values.taxIdentificationNumber),
        socialSecurityNumber: nullIfBlank(values.socialSecurityNumber),
        employmentStartDate: values.employmentStartDate,
      },
      {
        onSuccess: (created) => {
          form.reset();
          onRegistered(created);
        },
      },
    );
  };

  const customerOptions: readonly CustomerSummary[] = customers.data?.items ?? [];

  return (
    <Dialog
      open={open}
      onClose={register.isPending ? undefined : close}
      maxWidth="sm"
      fullWidth
      aria-labelledby="register-employee-title"
    >
      {/* Rule A. */}
      <DialogTitle id="register-employee-title">Register an Employee</DialogTitle>

      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <DialogContent>
          <Stack spacing={2}>
            <DialogContentText>
              This creates an employee record only. No account is created and no email is sent — you
              can invite them afterwards.
            </DialogContentText>

            {/* Rule C: rendered for the Accountant roles and for nobody else. */}
            {isAccountant && (
              <Controller
                name="customerId"
                control={form.control}
                render={({ field }) => (
                  <Autocomplete
                    options={customerOptions}
                    loading={customers.isLoading}
                    getOptionLabel={(option) => option.legalName}
                    isOptionEqualToValue={(option, value) => option.id === value.id}
                    value={customerOptions.find((option) => option.id === field.value) ?? null}
                    onChange={(_event, option) => {
                      field.onChange(option === null ? '' : option.id);
                    }}
                    onBlur={field.onBlur}
                    renderInput={(params) => (
                      <TextField
                        {...params}
                        label="Customer"
                        inputRef={field.ref}
                        error={form.formState.errors.customerId !== undefined}
                        helperText={
                          form.formState.errors.customerId?.message ??
                          'Active Customers only — a suspended Customer cannot gain Employees.'
                        }
                      />
                    )}
                  />
                )}
              />
            )}

            {/* The Customer list failing must not look like the form failing. Its own banner, no reload
                affordance: reopening the dialog refetches. */}
            {isAccountant && customers.error !== null && (
              <ErrorBanner error={customers.error} focusOnMount={false} />
            )}

            <EmployeeFieldset
              form={form}
              workEmailNotice={WORK_EMAIL_NOTICE_REGISTER}
              autoFocusFirstField={!isAccountant}
            />

            {/*
              Every server error lands here, above the submit button, rendered from `title`:
              403 "You may only register employees at your own customer." is a SCOPE failure, not a
              role failure, and one of the few 403s in this API that is not a bug in `can()`;
              409 "An employee with this work email already exists at this customer." is the
              per-Customer uniqueness constraint; 422 "This customer is not active." is rule D's race.
              ErrorBanner owns the whole taxonomy so this component branches on no status code itself.
            */}
            <ErrorBanner error={register.error} />
          </Stack>
        </DialogContent>

        <DialogActions>
          <Button onClick={close} disabled={register.isPending}>
            Cancel
          </Button>
          {/* Rule E: pending only. Never `disabled={!form.formState.isValid}`. */}
          <Button type="submit" variant="contained" loading={register.isPending}>
            Register
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
