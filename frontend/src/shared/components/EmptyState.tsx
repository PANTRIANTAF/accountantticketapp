import type { ReactNode } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Typography from '@mui/material/Typography';
import InboxOutlinedIcon from '@mui/icons-material/InboxOutlined';

/**
 * An icon, a sentence, and an optional action. GeneralUIArchitecture.md sections 7.4 and 8.3.
 *
 * EMPTY IS NOT AN ERROR. `items: []` with `totalCount: 0` is a list with nothing in it yet, and the
 * correct rendering names the reason and -- where the role permits it -- the action that fixes it:
 * "No customers yet" plus *Add Customer* for an AccountantAdmin, and the sentence alone for an
 * AccountantUser who cannot create one. The caller gates the action with can(); this component just
 * renders the slot it is given.
 *
 * `items: []` WITH `totalCount > 0` IS A DIFFERENT THING and the one case this component exists to
 * get right. The server answers a page past the end with items: [] and a 200, not a 404
 * (section 3.3), so the honest offer is "back to the first page" -- NOT "no results", which tells a
 * user with 400 rows that they have none. usePaginatedQuery computes the flag; pass it through.
 */
export function EmptyState({
  message,
  detail,
  action,
  isOverrunPage = false,
  onBackToFirstPage,
}: {
  /** "No customers yet." Written for this list, not a generic "No data". */
  message: string;
  detail?: string;
  /** The action that fixes it, already gated by can() at the call site. */
  action?: ReactNode;
  /** usePaginatedQuery's isOverrunPage. */
  isOverrunPage?: boolean;
  onBackToFirstPage?: () => void;
}) {
  const overrun = isOverrunPage;

  return (
    <Box sx={{ py: 6, px: 3, textAlign: 'center' }}>
      <InboxOutlinedIcon
        // Decorative: the sentence below carries the whole message, so announcing the icon would
        // just add noise (section 8.4).
        aria-hidden
        color="disabled"
        sx={{ fontSize: 48, mb: 1 }}
      />
      <Typography variant="h6" component="p" gutterBottom>
        {overrun ? 'There is nothing on this page' : message}
      </Typography>
      {(overrun || detail !== undefined) && (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
          {overrun
            ? 'This page is past the end of the results. Go back to the first page.'
            : detail}
        </Typography>
      )}
      {overrun
        ? onBackToFirstPage !== undefined && (
            <Button variant="outlined" onClick={onBackToFirstPage}>
              Back to the first page
            </Button>
          )
        : action}
    </Box>
  );
}
