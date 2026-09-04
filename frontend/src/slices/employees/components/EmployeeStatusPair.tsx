import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import { StatusChip } from '../../../shared/components/StatusChip';
import type { AccountStatus, EmployeeStatus } from '../../../shared/format/enums';

/**
 * THE TWO STATUS VOCABULARIES, TOGETHER, WITH THEIR LABELS -- and the only place either chip is
 * rendered on a detail screen. EmployeesScreens.md section 5.3, plan section 8.1.
 *
 * | | Field | Values | Owner | Changed by |
 * | Employment | `status`        | Active, Departed                   | employees table  | depart, reinstate |
 * | Access     | `accountStatus` | null, Invited, Active, Suspended    | Identity         | invite, suspend-account, reactivate-account, and depart as a side effect |
 *
 * A. THE PREFIX LABELS ARE PART OF THE COMPONENT, not of the screen. Both vocabularies contain the
 *    word "Active", so two bare chips reading "Active" and "Suspended" side by side are unreadable --
 *    and this component exists so that no screen can render one chip without its label. A single
 *    merged chip is worse still: a Departed Employee's account is Suspended, so merging destroys the
 *    distinction the entire Actions menu is built on.
 *
 * B. AN `Active` EMPLOYEE WITH A `Suspended` ACCOUNT IS A NORMAL STATE -- access revoked, still
 *    employed. This component RENDERS WHAT ARRIVED and infers neither status from the other, in
 *    either direction. `Departed` with `Active` access does not occur today only because
 *    `DepartEmployeeHandler` suspends in the same transaction; that is the server's invariant to keep,
 *    not a fact the UI may assert.
 *
 * C. `accountStatus === null` IS `Access: Not invited` -- not "Inactive", not "None", and never an
 *    empty chip, which is a rendered `undefined`. It means no account exists, which is a different
 *    fact from a suspended one. It is drawn as a plain outlined `Chip` rather than through
 *    `StatusChip`, because "Not invited" is not a member of any status vocabulary and passing it to
 *    `StatusChip` would need a cast and would land on that component's unknown-word fallback.
 *
 * D. THE COLOUR MAP LIVES IN THE SHARED `StatusChip` AND NOWHERE ELSE, so `Suspended` cannot be red
 *    here and green on another screen. Colour is never the only carrier: the word is always shown.
 *
 * Not rendered at all for the `Employee` role -- `EmployeeSelf` has neither field, so a chip built
 * from that shape would render `undefined` (plan section 8.2).
 */
export function EmployeeStatusPair({
  status,
  accountStatus,
}: {
  /** EMPLOYMENT. From `EmployeeDetail.status`. */
  status: EmployeeStatus;
  /** ACCESS. From `EmployeeDetail.accountStatus`; `null` means no account exists -- rule C. */
  accountStatus: AccountStatus | null;
}) {
  return (
    <Stack
      direction="row"
      spacing={3}
      sx={{ alignItems: 'center', flexWrap: 'wrap', rowGap: 1 }}
    >
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <Typography variant="body2" color="text.secondary" component="span">
          Employment:
        </Typography>
        <StatusChip status={status} />
      </Stack>

      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <Typography variant="body2" color="text.secondary" component="span">
          Access:
        </Typography>
        {/* Rule C. */}
        {accountStatus === null ? (
          <Chip label="Not invited" size="small" variant="outlined" />
        ) : (
          <StatusChip status={accountStatus} />
        )}
      </Stack>
    </Stack>
  );
}
