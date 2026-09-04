import { useState } from 'react';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { useReactivateCustomer } from '../queries';

/**
 * *Actions -> Reactivate Customer*, AccountantAdmin ONLY (CustomersActionCatalogue.cs:15). The mirror
 * of SuspendCustomerDialog: same gate, same ConfirmDialog requirement, same SetCustomerStatusRequestDto
 * `{ customerId, reason? }`, same CustomerDto response, and 422 "This customer is already active."
 * when the row has not moved (ReactivateCustomerHandler.cs:47-48).
 *
 * NOT RED. confirmColor stays "primary": this is the repair direction, and a repair is not styled as
 * a destruction (EmployeesScreens.md section 8.2).
 *
 * THE ONE HONEST OMISSION (section 4.6). This dialog must NOT promise that anybody can now sign in,
 * and the Snackbar the caller shows says "Customer reactivated" and nothing more. Reactivating the
 * Customer clears the Customer-level gate only; a UserAccount that was suspended individually keeps
 * its own status, owned by Identity, and that person still cannot sign in.
 * 02-AuthorizationMatrix.md section 11 calls this "correct and will look like a bug" -- so the second
 * sentence below states it rather than leaving the operator to discover it from a support call.
 */

/** CustomerValidation.NormalizeReason: optional, at most 500 characters -- same rule as suspend. */
const REASON_MAX_LENGTH = 500;

export function ReactivateCustomerDialog({
  open,
  customerId,
  legalName,
  onClose,
  onReactivated,
}: {
  open: boolean;
  customerId: string;
  legalName: string;
  onClose: () => void;
  onReactivated: () => void;
}) {
  const mutation = useReactivateCustomer();

  /** One optional field, so no resolver and no schema -- see SuspendCustomerDialog. */
  const [reason, setReason] = useState('');

  const confirm = () => {
    const trimmed = reason.trim();
    mutation.mutate(
      { customerId, reason: trimmed === '' ? null : trimmed },
      {
        onSuccess: () => {
          onReactivated();
        },
      },
    );
  };

  return (
    <ConfirmDialog
      open={open}
      title={`Reactivate ${legalName}?`}
      confirmLabel="Reactivate Customer"
      isPending={mutation.isPending}
      onConfirm={confirm}
      onClose={onClose}
    >
      <Stack spacing={2}>
        <Typography variant="body2">
          The Customer-level block on signing in is removed.
        </Typography>
        {/*
          THE SENTENCE THAT PREVENTS THE SUPPORT CALL. It is deliberately not softened into "everyone
          can sign in again": SuspendCustomerHandler and ReactivateCustomerHandler each change exactly
          one row in `customers` and touch no UserAccount, so an account suspended in Identity is
          untouched by both.
        */}
        <Typography variant="body2">
          Accounts that were suspended individually stay suspended. Reactivating the Customer does not
          restore them.
        </Typography>

        <TextField
          label="Reason (recorded in the audit log)"
          value={reason}
          onChange={(event) => {
            setReason(event.target.value);
          }}
          multiline
          minRows={2}
          slotProps={{ htmlInput: { maxLength: REASON_MAX_LENGTH } }}
          helperText={`Optional. At most ${String(REASON_MAX_LENGTH)} characters.`}
        />

        {/* 422 "This customer is already active." verbatim, dialog stays open, chip self-corrects via
            useReactivateCustomer's onError. */}
        <ErrorBanner error={mutation.error} />
      </Stack>
    </ConfirmDialog>
  );
}
