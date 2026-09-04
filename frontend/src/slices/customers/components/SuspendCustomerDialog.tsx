import { useState } from 'react';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { useSuspendCustomer } from '../queries';

/**
 * *Actions -> Suspend Customer*, AccountantAdmin ONLY (CustomersActionCatalogue.cs:14).
 *
 * ConfirmDialog IS MANDATORY HERE (GeneralUIArchitecture.md section 8.3) AND IT NAMES THE
 * CONSEQUENCE INSTEAD OF ASKING "Are you sure?". From the operator's seat this looks like a chip
 * changing colour; it is in fact a lockout of every person at that company. The four sentences below
 * are each a fact read out of the code, not reassurance -- CustomersScreens.md section 4.5 fixes both
 * their content and the reason each one has to be there:
 *
 *   1. LoginHandler calls ICustomerApi.IsActiveAsync live on EVERY login for the two Customer-side
 *      roles and refuses with the same generic 401 it gives a wrong password, so the person locked out
 *      cannot tell suspension from a typo (02-AuthorizationMatrix.md section 11). "From their next
 *      attempt" is therefore exact.
 *   2. An Accountant's CustomerId is null and the check is skipped.
 *   3. NOTHING RE-CHECKS CUSTOMER STATUS ON COOKIE REPLAY. SuspendCustomerHandler changes exactly one
 *      row in `customers` and touches no UserAccount, so this is NOT a session revocation and the
 *      dialog must not imply it is. An operator suspending a Customer to stop someone working right
 *      now has to know it does not do that.
 *   4. Reactivating later does NOT restore individually suspended accounts -- those have their own
 *      status, owned by Identity. 02-AuthorizationMatrix.md section 11 calls this "correct and will
 *      look like a bug", which is precisely why it is disclosed before the click and not discovered
 *      after it.
 *
 * confirmColor="error": this is the destructive direction. Reactivate is a repair and is not red
 * (EmployeesScreens.md section 8.2).
 */

/** CustomerValidation.NormalizeReason: optional, at most 500 characters. */
const REASON_MAX_LENGTH = 500;

export function SuspendCustomerDialog({
  open,
  customerId,
  legalName,
  onClose,
  onSuspended,
}: {
  open: boolean;
  customerId: string;
  /** Named in the title, so the operator confirms a Customer and not "this record". */
  legalName: string;
  onClose: () => void;
  onSuspended: () => void;
}) {
  const mutation = useSuspendCustomer();

  /**
   * ONE OPTIONAL FIELD, SO NO react-hook-form AND NO ZOD SCHEMA. schemas.ts exports four schemas and
   * none of them is this: the only rule is a length the input enforces with maxLength, and there is
   * nothing to validate on blur. A resolver here would add a second source of truth for one number.
   */
  const [reason, setReason] = useState('');

  const confirm = () => {
    const trimmed = reason.trim();
    mutation.mutate(
      // null, NEVER '' (section 9.3). CustomerValidation.NormalizeReason maps an empty reason to
      // null anyway, but '' on the wire means "the operator left a blank reason", which is a
      // different claim from "no reason given".
      { customerId, reason: trimmed === '' ? null : trimmed },
      {
        onSuccess: () => {
          onSuspended();
        },
      },
    );
  };

  return (
    <ConfirmDialog
      open={open}
      title={`Suspend ${legalName}?`}
      confirmLabel="Suspend Customer"
      confirmColor="error"
      isPending={mutation.isPending}
      onConfirm={confirm}
      onClose={onClose}
    >
      <Stack spacing={2}>
        <Typography variant="body2">
          Every Customer Admin and Employee at this Customer will be unable to sign in, from their
          next attempt.
        </Typography>
        <Typography variant="body2">
          Accountants are unaffected.
        </Typography>
        <Typography variant="body2">
          Anyone already signed in keeps working until their session expires, up to 8 hours.
          Suspending the Customer does not sign anybody out.
        </Typography>
        <Typography variant="body2">
          Reactivating later does not restore individually suspended accounts.
        </Typography>

        {/*
          THE LABEL SAYS WHERE THE REASON GOES, BECAUSE IT GOES SOMEWHERE THE OPERATOR CANNOT SEE.
          It is written into the After payload of the CustomerSuspended audit entry and nowhere else:
          not onto the Customer row, not into CustomerDto, not onto any screen. A label implying the
          Customer will read it is false; one implying it appears on this screen afterwards is worse,
          because the operator will go looking for it (section 4.5).
        */}
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

        {/*
          422 "This customer is already suspended." (SuspendCustomerHandler.cs:49-50) IS REACHABLE
          FROM A STALE TAB whose chip still reads Active. Rendered verbatim, THE DIALOG STAYS OPEN,
          and the detail query is invalidated so the chip corrects itself -- that invalidation is a
          cache decision and lives in useSuspendCustomer's onError, not here (section 4.5).
        */}
        <ErrorBanner error={mutation.error} />
      </Stack>
    </ConfirmDialog>
  );
}
