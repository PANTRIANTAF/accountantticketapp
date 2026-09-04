import Alert from '@mui/material/Alert';
import Link from '@mui/material/Link';
import { Link as RouterLink } from 'react-router-dom';

/**
 * "This is version 3 of 5. It is not the current version." Screens/TicketTypesScreens.md section 7.2.
 *
 * MANDATORY WHENEVER versionNumber !== currentVersionNumber, above everything else on the detail
 * screen -- and the historical view offers NO Edit button at all, only the link out of here.
 *
 * WHY THE ABSENCE OF THIS BANNER IS SILENT DATA LOSS. /edit replaces the field set WHOLESALE from
 * whatever the form holds (EditTicketTypeHandler.cs, full-replacement semantics). An Accountant who
 * steps back to v1, spots a typo and presses Edit gets a form full of v1's fields; saving mints v6
 * containing v1's fields and reverts four versions of work. Every response is 200, no audit entry
 * says "reverted", and the only visible trace is a version counter that went up by one -- which is
 * exactly what a successful edit looks like.
 *
 * severity="info", not "warning": looking at an old version is a legitimate thing to be doing. The
 * warning belongs on the ACTION, which is why the action is removed instead of being flagged.
 */
export function VersionBanner({
  ticketTypeId,
  versionNumber,
  currentVersionNumber,
}: {
  ticketTypeId: string;
  /** The version these `fields` came from. */
  versionNumber: number;
  /** The latest version that exists. */
  currentVersionNumber: number;
}) {
  return (
    <Alert severity="info" sx={{ mb: 2 }}>
      This is version {versionNumber} of {currentVersionNumber}. It is not the current version.{' '}
      {/* No `?version=`: dropping the parameter is what makes this the CURRENT view, and it routes
          through /detail, which is the only read that applies the IsActive check. */}
      <Link component={RouterLink} to={`/ticket-types/${ticketTypeId}`}>
        View the current version
      </Link>
    </Alert>
  );
}
