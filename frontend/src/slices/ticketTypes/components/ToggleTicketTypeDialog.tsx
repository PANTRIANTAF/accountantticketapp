import Typography from '@mui/material/Typography';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';

/**
 * The DEACTIVATION confirmation. Screens/TicketTypesScreens.md section 3.3.
 *
 * DEACTIVATION IS REVERSIBLE, SO IT IS NOT STYLED AS DESTRUCTIVE -- confirmColor stays `primary`.
 * But it is invisible from the other side of the Customer boundary, so the dialog must state all
 * four consequences or an Accountant cannot predict what happens. All four are below, and none of
 * them is decoration:
 *
 *   1. Customer Admins and Employees stop seeing the type ENTIRELY -- not greyed out, not "closed
 *      for new tickets", absent. /detail answers them 404 via ApplyCustomerSideVisibility
 *      (TicketTypeMapper.cs:33-37). 02-AuthorizationMatrix.md section 5: "is NOT returned by the API
 *      at all... not greyed out in the UI".
 *   2. Existing tickets are unaffected and still render, because /version deliberately keeps working
 *      (correction note T-4).
 *   3. Nothing is deleted. Reactivating restores exactly the previous state, with the same version
 *      number and the same history. There is no delete endpoint for a type or a version and
 *      02-AuthorizationMatrix.md section 5 grants delete to nobody.
 *   4. Accountants keep it in their *All* and *Inactive* lists, which is the only way back -- a type
 *      that vanished from every list could never be reactivated.
 *
 * REACTIVATION NEEDS NO CONFIRMATION and does not come through here: it only makes something visible
 * again. The call sites mutate directly for that direction.
 */
export function ToggleTicketTypeDialog({
  open,
  displayName,
  code,
  isPending,
  error,
  onConfirm,
  onClose,
}: {
  open: boolean;
  displayName: string;
  /** The immutable human handle, so the dialog names the row unambiguously. */
  code: string;
  isPending: boolean;
  /** The toggle mutation's error, if the previous attempt failed. */
  error: unknown;
  onConfirm: () => void;
  onClose: () => void;
}) {
  return (
    <ConfirmDialog
      open={open}
      title={`Deactivate ${displayName}?`}
      confirmLabel="Deactivate"
      // Reversible, so NOT the destructive red. ConfirmDialog reserves that for one-way operations.
      confirmColor="primary"
      isPending={isPending}
      onConfirm={onConfirm}
      onClose={onClose}
    >
      <Typography variant="body2" gutterBottom>
        {displayName} ({code}) will be deactivated. This is reversible, but it changes what other
        people can see:
      </Typography>

      <Typography variant="body2" component="ul" sx={{ pl: 3, mb: 2 }}>
        <li>
          Customer Admins and Employees stop seeing this ticket type completely. It disappears from
          their list, and opening its link gives them &ldquo;Not found&rdquo; — it is not greyed out
          and it does not say it was retired.
        </li>
        <li>
          Tickets already raised against it are unaffected and still show their form, because the
          version they were raised against stays readable.
        </li>
        <li>
          Nothing is deleted. Reactivating restores exactly this state, with the same version number
          and the same history.
        </li>
        <li>
          You and other Accountants keep seeing it, under the <em>All</em> and <em>Inactive</em>{' '}
          filters — that is where you reactivate it from.
        </li>
      </Typography>

      {/* A failed row action renders above the affordance that triggered it, and focus does not move
          inside a dialog that is already focused. */}
      <ErrorBanner error={error} focusOnMount={false} />
    </ConfirmDialog>
  );
}
