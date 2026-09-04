import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import * as z from 'zod';
import Button from '@mui/material/Button';
import Dialog from '@mui/material/Dialog';
import DialogActions from '@mui/material/DialogActions';
import DialogContent from '@mui/material/DialogContent';
import DialogTitle from '@mui/material/DialogTitle';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { useUpdateCustomerContact } from '../queries';
import { contactSchema } from '../schemas';

/**
 * *Edit contact* -- ONE COMPONENT, TWO SCREENS. /customers/:customerId mounts it and so does
 * /my-customer, and it knows nothing about which one it is on: it takes a customerId and the seven
 * current values, and posts UpdateCustomerContactRequestDto's eight keys (section 8 rule F).
 *
 * THAT REUSE IS SAFE BECAUSE THE SERVER MAKES IT SAFE, not because the component is careful.
 * CustomerScope restricts the write to the caller's own row regardless
 * (UpdateCustomerContactHandler.cs:39-43), so there is no second endpoint, no CustomerAdmin-specific
 * hook and no CustomerAdmin-specific form. A CustomerAdmin passing somebody else's customerId gets
 * 404, not a write.
 *
 * ROLES AA, AU AND CA (CustomersActionCatalogue.cs:18-19) -- the only write in this slice a
 * Customer-side user may perform, and the reason the contact half of the record is split out from the
 * legal half at all. An Employee is NOT in that list, so /my-customer renders no button for them and
 * never mounts this dialog.
 *
 * NO 409 IS POSSIBLE HERE. Contact details are not unique, and CustomersEndpoints.cs:70-72 declares
 * none. A reload affordance is therefore not offered: 403, 404 and 422 are all ErrorBanner needs to
 * present, and 422 is the only one whose title is rendered verbatim.
 *
 * THE SEVEN FIELDS ARE DISJOINT FROM THE LEGAL DIALOG'S FIVE, exactly. Both endpoints are full
 * replacements (UpdateCustomerContactHandler.cs:47-53), so adding legalName here would either
 * silently revert it -- it is absent from this DTO -- or 403 for the CustomerAdmin this dialog exists
 * to serve.
 */

type ContactInput = z.input<typeof contactSchema>;
type ContactOutput = z.output<typeof contactSchema>;

/**
 * The seven current values, in the shape both callers already hold: `Customer` on the detail screen
 * and `CustomerSelf` on /my-customer both carry these seven keys with these exact types, so neither
 * caller converts anything and this component never learns which DTO it came from.
 */
export interface CustomerContactValues {
  addressLine1: string;
  addressLine2: string | null;
  addressCity: string;
  addressPostalCode: string;
  addressCountry: string;
  contactEmail: string;
  contactPhone: string;
}

export function EditCustomerContactDialog({
  open,
  customerId,
  initialValues,
  onClose,
  onSaved,
}: {
  open: boolean;
  customerId: string;
  initialValues: CustomerContactValues;
  onClose: () => void;
  onSaved: () => void;
}) {
  const mutation = useUpdateCustomerContact();

  /**
   * The parent mounts this component only while the dialog is open, so these defaultValues are read
   * from the freshest record the cache holds on every open (section 8 rule A) and nothing re-seeds
   * them afterwards -- a failed submit leaves every typed value intact.
   *
   * addressLine2 is the one optional: '' in the input, null in the parsed output (section 8 rule C).
   */
  const form = useForm<ContactInput, unknown, ContactOutput>({
    resolver: zodResolver(contactSchema),
    mode: 'onBlur',
    defaultValues: {
      addressLine1: initialValues.addressLine1,
      addressLine2: initialValues.addressLine2 ?? '',
      addressCity: initialValues.addressCity,
      addressPostalCode: initialValues.addressPostalCode,
      addressCountry: initialValues.addressCountry,
      contactEmail: initialValues.contactEmail,
      contactPhone: initialValues.contactPhone,
    },
  });

  /** ALL EIGHT KEYS, ALWAYS, including the unchanged ones. */
  const onSubmit = (values: ContactOutput) => {
    mutation.mutate(
      { customerId, ...values },
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
        <DialogTitle>Edit contact details</DialogTitle>
        <DialogContent>
          {/*
            THE COMPANY'S ADDRESS AND THE COMPANY'S CONTACT DETAILS -- never "their address", which
            reads as a person's (section 12.1 rule B). `label` is a real <label>; a placeholder
            disappears on focus and is invisible to a screen reader (section 8.4 item 1).
          */}
          <Stack spacing={2}>
            <TextField
              {...form.register('addressLine1')}
              label="Address line 1"
              required
              autoFocus
              error={form.formState.errors.addressLine1 !== undefined}
              helperText={form.formState.errors.addressLine1?.message}
            />
            <TextField
              {...form.register('addressLine2')}
              label="Address line 2"
              error={form.formState.errors.addressLine2 !== undefined}
              helperText={form.formState.errors.addressLine2?.message ?? 'Optional.'}
            />
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                {...form.register('addressCity')}
                label="City"
                required
                fullWidth
                error={form.formState.errors.addressCity !== undefined}
                helperText={form.formState.errors.addressCity?.message}
              />
              <TextField
                {...form.register('addressPostalCode')}
                label="Postal code"
                required
                fullWidth
                error={form.formState.errors.addressPostalCode !== undefined}
                helperText={form.formState.errors.addressPostalCode?.message}
              />
              <TextField
                {...form.register('addressCountry')}
                label="Country"
                required
                fullWidth
                error={form.formState.errors.addressCountry !== undefined}
                helperText={form.formState.errors.addressCountry?.message}
              />
            </Stack>
            {/*
              type="email" for the keyboard and nothing else -- validation is the schema's, and the
              schema checks only for '@', because CustomerValidation.cs:56-62 checks only for '@'.
              noValidate on the form keeps the browser from adding a stricter rule of its own.
            */}
            <TextField
              {...form.register('contactEmail')}
              label="Contact email"
              type="email"
              required
              error={form.formState.errors.contactEmail !== undefined}
              helperText={form.formState.errors.contactEmail?.message}
            />
            <TextField
              {...form.register('contactPhone')}
              label="Contact phone"
              required
              error={form.formState.errors.contactPhone !== undefined}
              helperText={form.formState.errors.contactPhone?.message}
            />
          </Stack>

          {/* The form-level banner, immediately above the submit button (section 7.3). No onReload:
              there is no 409 on this endpoint to offer one for. */}
          <ErrorBanner error={mutation.error} />
        </DialogContent>

        <DialogActions>
          <Button onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="contained" loading={mutation.isPending}>
            Save
          </Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
