import Chip from '@mui/material/Chip';

/**
 * A ticket type's isActive bool as the word "Active" or "Inactive", in a Chip.
 *
 * WHY THIS EXISTS INSTEAD OF shared/components/StatusChip. A DOCUMENTED CONFLICT, resolved in favour
 * of the higher-precedence document and reported rather than hidden.
 *
 *   Screens/TicketTypesScreens.md section 3.1 rule C says: "Map `true` -> "Active" and
 *   `false` -> "Inactive" and pass the word" to StatusChip.
 *
 *   GeneralUIArchitecture.md section 8.3 -- which outranks a screen document -- defines StatusChip's
 *   vocabulary as exactly `Active`/`Suspended`/`Invited`/`Departed`, and section 10.1 closes it
 *   further: there are FOUR status vocabularies, "Inactive" is in none of them, and the protection is
 *   at the call site -- "pass a value typed with its own vocabulary, never a bare string".
 *   shared/components/StatusChip.tsx implements that as
 *   `StatusWord = CustomerStatus | AccountStatus | EmployeeStatus | AuditOutcome`, so
 *   `<StatusChip status="Inactive" />` does not typecheck, and shared/ may not be modified by this
 *   slice.
 *
 * So the word rule 3.1 rule C requires is honoured, and StatusChip's closed union is left closed. The
 * two colours are chosen to agree with the shared map's own semantics rather than to invent a third
 * scheme: `success` for Active, exactly as there, and `default` for Inactive, which is that map's
 * colour for `Departed` -- "historical rather than active. Not an error and not a warning".
 *
 * "SUSPENDED" IS NOT AVAILABLE HERE AND MUST NOT BE BORROWED. It is a Customer and account state in
 * 00-Glossary.md; reusing it for a ticket type makes two different things wear one colour.
 *
 * THE WORD IS ALWAYS SHOWN. Colour is never the only carrier of meaning (section 8.4).
 */
export function TicketTypeStatusChip({
  isActive,
  size = 'small',
}: {
  isActive: boolean;
  size?: 'small' | 'medium';
}) {
  return (
    <Chip
      label={isActive ? 'Active' : 'Inactive'}
      color={isActive ? 'success' : 'default'}
      size={size}
      variant="outlined"
    />
  );
}
