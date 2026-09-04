import Chip from '@mui/material/Chip';
import type {
  AccountStatus,
  AuditOutcome,
  CustomerStatus,
  EmployeeStatus,
} from '../format/enums';

/**
 * Every status word this SPA can render, across all FOUR vocabularies (section 10.1). No two
 * vocabularies are the same and `Invited` belongs to exactly one of them.
 *
 * SHARING THE COLOUR MAP DOES NOT MAKE EVERY WORD VALID FOR EVERY ENTITY. A Customer is NEVER
 * `Invited` -- CustomerStatus declares two members, both insert paths write `Active`, and migration
 * 20260901_002_AddCustomerStatusCheck.sql adds CHECK (status IN ('Active','Suspended')). The
 * protection is at the CALL SITE: pass a value typed with its own vocabulary
 * (`status: CustomerStatus`), never a bare string, and TypeScript refuses `Invited` for a Customer.
 */
export type StatusWord = CustomerStatus | AccountStatus | EmployeeStatus | AuditOutcome;

type ChipColour = 'default' | 'success' | 'warning' | 'error' | 'info';

/**
 * ONE COLOUR PER WORD, application-wide, so `Suspended` is never green on one screen and red on
 * another (GeneralUIArchitecture.md section 8.3). Colours are semantic palette NAMES, never hex --
 * the values live in theme.ts, which is the only file allowed to hold one.
 */
const STATUS_COLOURS: Record<StatusWord, ChipColour> = {
  // Working normally.
  Active: 'success',
  Success: 'success',

  // A real state, not a problem: the invitation is outstanding and the person has not chosen a
  // password yet.
  Invited: 'warning',
  // A permission denial. Audited, expected, and not a system failure.
  Denied: 'warning',

  // Blocked by an administrator. Deliberate, and it stops the person logging in.
  Suspended: 'error',
  // Something went wrong on the server side of the audited operation.
  Failure: 'error',

  // Historical rather than active. Not an error and not a warning: the person left.
  Departed: 'default',
};

/**
 * A status word as a coloured Chip.
 *
 * THE WORD IS ALWAYS SHOWN. Colour is never the only carrier of meaning (section 8.4) -- a
 * red-versus-green dot is invisible to a colour-blind user and to a screen reader alike.
 */
export function StatusChip({
  status,
  size = 'small',
}: {
  status: StatusWord;
  size?: 'small' | 'medium';
}) {
  // The `?? 'default'` is for a word a future migration adds and this file does not know yet:
  // render it plainly rather than crash or -- worse -- hide the row's state.
  const colour = STATUS_COLOURS[status] ?? 'default';
  return <Chip label={status} color={colour} size={size} variant="outlined" />;
}
