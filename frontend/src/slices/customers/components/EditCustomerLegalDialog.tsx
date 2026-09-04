import { useForm } from 'react-hook-form';
import { useQueryClient } from '@tanstack/react-query';
import { zodResolver } from '@hookform/resolvers/zod';
import * as z from 'zod';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogContentText from '@mui/material/DialogContentText';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { customerKeys, useUpdateCustomerLegal } from '../queries';
import { legalSchema } from '../schemas';
import type { Customer } from '../types';

/**
 * *Edit legal* on /customers/:customerId. AccountantAdmin and AccountantUser ONLY
 * (CustomersActionCatalogue.cs:17); a CustomerAdmin gets 403 from
 * UpdateCustomerLegalHandler.cs:39, which is why this is a separate dialog from the contact one and
 * not a section of a shared "edit customer" form.
 *
 * THE SPLIT IS A PERMISSION BOUNDARY AND IT IS EXACT. This dialog posts
 * UpdateCustomerLegalRequestDto's five keys and no others; the contact dialog posts its eight. Both
 * endpoints are FULL REPLACEMENTS (UpdateCustomerLegalHandler.cs:53-56), so a field in the wrong
 * dialog is either silently reverted -- it is absent from the DTO this one posts -- or a 403.
 *
 * NO onboardedOn INPUT. It is read-only on the Record card: no endpoint changes it
 * (UpdateCustomerLegalRequestDto.cs:5-9). The plan's section 15 asks whether that is intended.
 *
 * FOUR STATUS CODES ARRIVE IN A FIXED ORDER, because the handler runs RequireAsync -> scope read ->
 * validate -> duplicate check: 403, then 404, then 422, then 409. All four are rendered by one
 * form-level ErrorBanner above the submit button, with every typed value intact, and NONE is mapped
 * onto a field -- ProblemDetails here has no errors{} dictionary to map from
 * (AppExceptionMiddleware.cs:53-58).
 *
 *   403  ErrorBanner renders "You do not have permission to do that." and NOT the server's title,
 *        which is the internal string "Permission denied for action 'EditCustomerLegal'."
 *        (PermissionChecker.cs:63). The verbatim-title rule governs 400, 409 and 422, where the
 *        wording was written for a user.
 *   409  "A customer with this tax number already exists." -- the ONLY 409 in this slice
 *        (CustomersEndpoints.cs:84). UpdateCustomerLegalHandler raises it twice, at :48-50 as a
 *        pre-check and at :62-65 by catching SQLSTATE 23505 for the concurrent case, with the SAME
 *        title both times, so there is no special case for the race. Rendered verbatim WITH a Reload
 *        affordance, which ErrorBanner adds for a 409 when onReload is supplied.
 *   422  Any limit this schema failed to mirror. Rendered verbatim.
 */

type LegalInput = z.input<typeof legalSchema>;
type LegalOutput = z.output<typeof legalSchema>;

export function EditCustomerLegalDialog({
  open,
  customer,
  onClose,
  onSaved,
}: {
  open: boolean;
  /** The freshest detail the cache holds. Section 8 rule A: never a stale read. */
  customer: Customer;
  onClose: () => void;
  onSaved: () => void;
}) {
  const mutation = useUpdateCustomerLegal();
  const queryClient = useQueryClient();

  /**
   * The INPUT type is all strings -- an untouched optional is '' in the field and becomes null in the
   * parsed OUTPUT, which is what the DTO wants (section 8 rule C). CustomerValidation.cs:74-82 maps
   * an empty optional to null, and '' is a value that can pass a nullability check and fail a length
   * one.
   *
   * THE PARENT MOUNTS THIS COMPONENT ONLY WHILE THE DIALOG IS OPEN, which is what makes these
   * defaultValues correct without a reset effect: they are read from the freshest cached detail on
   * every open (section 8 rule A), and nothing re-seeds them afterwards, so a background refetch
   * cannot wipe what the user has typed and a failed submit leaves every value intact.
   */
  const form = useForm<LegalInput, unknown, LegalOutput>({
    resolver: zodResolver(legalSchema),
    mode: 'onBlur',
    defaultValues: {
      legalName: customer.legalName,
      tradingName: customer.tradingName ?? '',
      taxNumber: customer.taxNumber,
      taxOffice: customer.taxOffice ?? '',
    },
  });

  /** ALL FIVE KEYS, ALWAYS, including the unchanged ones. There is no partial semantic to reach for. */
  const onSubmit = (values: LegalOutput) => {
    mutation.mutate(
      { customerId: customer.id, ...values },
      {
        onSuccess: () => {
          onSaved();
        },
      },
    );
  };

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <form
        onSubmit={(event) => {
          void form.handleSubmit(onSubmit)(event);
        }}
        noValidate
      >
        <DialogTitle>Edit legal details</DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ mb: 2 }}>
            These fields identify the Customer as a legal entity. A Customer Admin cannot change
            them.
          </DialogContentText>

          {/* A COMPANY'S FIELDS. No "First name", no person-shaped placeholder: a Customer is never a
              natural person (00-Glossary.md; section 12.1 rule B). And `label` is a real <label> --
              a placeholder is never a label (section 8.4 item 1). */}
          <Stack spacing={2}>
            <TextField
              {...form.register('legalName')}
              label="Legal name"
              required
              autoFocus
              error={form.formState.errors.legalName !== undefined}
              helperText={form.formState.errors.legalName?.message}
            />
            <TextField
              {...form.register('tradingName')}
              label="Trading name"
              error={form.formState.errors.tradingName !== undefined}
              helperText={form.formState.errors.tradingName?.message ?? 'Optional.'}
            />
            <TextField
              {...form.register('taxNumber')}
              label="Tax number"
              required
              error={form.formState.errors.taxNumber !== undefined}
              helperText={
                form.formState.errors.taxNumber?.message ?? 'Must be unique across all Customers.'
              }
            />
            <TextField
              {...form.register('taxOffice')}
              label="Tax office"
              error={form.formState.errors.taxOffice !== undefined}
              helperText={form.formState.errors.taxOffice?.message ?? 'Optional.'}
            />
          </Stack>

          {/*
            THE FORM-LEVEL BANNER, IMMEDIATELY ABOVE THE SUBMIT BUTTON (section 7.3). onReload is
            supplied so the 409 carries the reload affordance the taxonomy asks for; ErrorBanner adds
            it for a 409 and for nothing else.
          */}
          <ErrorBanner
            error={mutation.error}
            onReload={() => {
              void queryClient.invalidateQueries({
                queryKey: customerKeys.detail(customer.id),
              });
            }}
          />
        </DialogContent>

        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          {/* Disabled ONLY while pending (section 9.3). Never disabled on "the form is not dirty" or
              "there are errors": a disabled Save with no visible reason is unexplainable. */}
          <Button type="submit" variant="contained" loading={mutation.isPending}>
            Save
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
