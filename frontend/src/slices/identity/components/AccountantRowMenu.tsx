import { useState } from 'react';
import IconButton from '@mui/material/IconButton';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import MoreVertIcon from '@mui/icons-material/MoreVert';
import { UserRole } from '../../../shared/format/enums';
import { can } from '../../../shared/permissions/can';
import type { AccountantDetail } from '../types';

/**
 * The four row affordances of the accountant list, and nothing else. It fetches nothing, mutates
 * nothing and holds no state but the menu anchor: it receives the row, the caller's role and four
 * callbacks (IdentityScreens.md section 4.3).
 *
 * EVERY CONDITION BELOW IS VERIFIED AGAINST THE HANDLER THAT ENFORCES IT.
 *
 *   Suspend      can(SuspendAccountant)    + not own row + status === 'Active'
 *                                          SuspendAccountantHandler.cs:45, :51
 *   Reactivate   can(ReactivateAccountant) + status === 'Suspended'
 *                                          ReactivateAccountantHandler.cs:49-54
 *   Promote      can(PromoteAccountant)    + role === AccountantUser
 *                                          PromoteAccountantHandler.cs:44-45
 *   Demote       can(DemoteAccountant)     + not own row + role === AccountantAdmin
 *                                          DemoteAccountantHandler.cs:42, :48-49
 *
 * A. `if (row.role)` TO CHOOSE BETWEEN PROMOTE AND DEMOTE IS THE LIKELIEST BUG ON THIS SCREEN.
 *    AccountantAdmin is 0, so that test is false for EVERY Accountant Admin in the table: Promote
 *    appears on every Admin and Demote on nobody. Always === a named constant.
 * B. TWO ACTIONS ARE HIDDEN ON THE OWN ROW, NOT FOUR. AccountInvariants.RequireNotSelf is called from
 *    exactly two handlers -- suspend (:45) and demote (:42). Reactivate carries no self guard because
 *    a suspended Admin cannot make the call at all, and promote's answer to self-promotion is already
 *    "That account is already an Accountant Admin." Hiding four would invent a guard the server does
 *    not have.
 * C. HIDING IS AN AFFORDANCE, NEVER A GUARANTEE (section 6.2 rule B). A stale list, a second tab or a
 *    can.ts edited without this file all put the request on the wire, so the screen renders the 422 in
 *    a banner above the table. A catch that swallows it is forbidden.
 * D. PROMOTE STAYS VISIBLE ON AN Invited OR Suspended ROW. PromoteAccountantHandler.cs:47-49 allows it
 *    deliberately -- "The role is what they will be when they can act; it is not itself permission to
 *    act, which is what Status governs."
 * E. WHEN NO ITEM WOULD BE SHOWN, NO BUTTON IS DRAWN -- not a button opening an empty Menu. An
 *    Accountant Admin looking at this list is necessarily Active and AccountantAdmin, so on their OWN
 *    row all four conditions are false and this component renders null. That is the specified
 *    behaviour, and the `(you)` label beside the name is what stops it reading as a bug.
 * F. Prefer hiding to disabling (section 6.2 rule C). Nothing in 02-AuthorizationMatrix.md sections
 *    1-2 or 11 names a case on this screen that must stay visible but greyed out.
 */
export function AccountantRowMenu({
  row,
  role,
  isOwnRow,
  onSuspend,
  onReactivate,
  onPromote,
  onDemote,
}: {
  row: AccountantDetail;
  /** The CALLER's role, for can(). Never the row's. */
  role: UserRole;
  /** Computed with isSameAccount() by the table, so the case rule lives in one place. */
  isOwnRow: boolean;
  onSuspend: () => void;
  onReactivate: () => void;
  onPromote: () => void;
  onDemote: () => void;
}) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);

  const showSuspend =
    can(role, 'SuspendAccountant') && !isOwnRow && row.status === 'Active';
  const showReactivate = can(role, 'ReactivateAccountant') && row.status === 'Suspended';
  const showPromote = can(role, 'PromoteAccountant') && row.role === UserRole.AccountantUser;
  const showDemote =
    can(role, 'DemoteAccountant') && !isOwnRow && row.role === UserRole.AccountantAdmin;

  // Rule E.
  if (!showSuspend && !showReactivate && !showPromote && !showDemote) return null;

  const close = () => setAnchor(null);

  /** Closes first, then acts, so the menu is never left open over a confirmation dialog. */
  const choose = (action: () => void) => () => {
    close();
    action();
  };

  return (
    <>
      {/* An icon-only button, so it carries an aria-label naming the row -- a table of eleven
          identical "More actions" buttons is unusable with a screen reader (section 8.4 item 4). */}
      <IconButton
        aria-label={`Actions for ${row.displayName}`}
        aria-haspopup="menu"
        size="small"
        onClick={(event) => setAnchor(event.currentTarget)}
      >
        <MoreVertIcon fontSize="small" />
      </IconButton>

      <Menu anchorEl={anchor} open={anchor !== null} onClose={close}>
        {showSuspend && (
          <MenuItem
            aria-label={`Suspend ${row.displayName}`}
            onClick={choose(onSuspend)}
          >
            Suspend
          </MenuItem>
        )}
        {showReactivate && (
          <MenuItem
            aria-label={`Reactivate ${row.displayName}`}
            onClick={choose(onReactivate)}
          >
            Reactivate
          </MenuItem>
        )}
        {showPromote && (
          <MenuItem
            aria-label={`Promote ${row.displayName} to Accountant Admin`}
            onClick={choose(onPromote)}
          >
            Promote to Accountant Admin
          </MenuItem>
        )}
        {showDemote && (
          <MenuItem
            aria-label={`Demote ${row.displayName} to Accountant User`}
            onClick={choose(onDemote)}
          >
            Demote to Accountant User
          </MenuItem>
        )}
      </Menu>
    </>
  );
}

/**
 * The own-row test, CASE-INSENSITIVE, mirroring AccountInvariants.cs:89 --
 * `string.Equals(targetId.ToString(), callerId, StringComparison.OrdinalIgnoreCase)`.
 *
 * The server compares that way deliberately, to defeat a "D"-versus-"N" Guid format mismatch that
 * would otherwise silently never match and turn the guard off. A case-sensitive === here would hide
 * the two actions on the own row only when the two spellings happened to agree.
 *
 * Exported because the table needs the same answer for the `(you)` label, and two copies of one
 * comparison is how one of them ends up case-sensitive.
 */
export function isSameAccount(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}
