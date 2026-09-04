import type { ReactNode } from 'react';
import Box from '@mui/material/Box';
import Link from '@mui/material/Link';
import Typography from '@mui/material/Typography';
import { Link as RouterLink } from 'react-router-dom';
import type { PaginatedResponse } from '../../../shared/api/paginated';
import { PaginatedTable, type Column } from '../../../shared/components/PaginatedTable';
import { StatusChip } from '../../../shared/components/StatusChip';
import { auditRoleLabel, dashIfEmpty, formatOccurredAt, middleTruncate } from '../auditFormat';
import type { AuditEntry } from '../types';

/**
 * The results table. AuditScreens.md section 3.1.
 *
 * FIVE COLUMNS, and every one of them is on AuditEntryDto (AuditEntryDto.cs:16-28). No column is
 * computed from two fields and none is invented: a "user" column joining actorUserId to a name would
 * need an id-to-name endpoint that does not exist (punch-list item 23).
 *
 * THROUGH PaginatedTable, WHICH IS THE ONLY TABLE IN THE APPLICATION (GeneralUIArchitecture.md
 * sections 8.2-8.3). It owns the single 1-based/0-based conversion, renders the pager from
 * response.pageSize -- never from the pageSize that was sent, which the server silently clamps to 50
 * -- and puts the error banner and the empty state inside the body so the header and pager stay put.
 * Composing Table + TablePagination here, or reaching for @mui/x-data-grid, is banned.
 *
 * NO SORT CONTROLS. The order is fixed server-side: OrderByDescending(OccurredAt).ThenByDescending(Id)
 * (SearchAuditLogHandler.cs:65), with no sort parameter on SearchAuditLogRequestDto. A clickable
 * header would either do nothing or sort one page of a hundred thousand rows, which is worse than
 * nothing because it looks like it worked. Newest first is the only order.
 *
 * NO ROW SELECTION, NO CHECKBOXES, NO BULK ACTIONS, NO ROW MENU. There is nothing to do to an audit
 * entry: the log is append-only and every route in AuditEndpoints.cs is a read.
 */
export function AuditEntryTable({
  data,
  isLoading,
  isFetching,
  error,
  isOverrunPage,
  onPageChange,
  onPageSizeChange,
  emptyMessage,
  emptyDetail,
  emptyAction,
}: {
  data: PaginatedResponse<AuditEntry> | undefined;
  isLoading: boolean;
  isFetching: boolean;
  error?: unknown;
  isOverrunPage: boolean;
  onPageChange: (pageNumber: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  emptyMessage: string;
  emptyDetail?: string;
  emptyAction?: ReactNode;
}) {
  return (
    <PaginatedTable<AuditEntry>
      data={data}
      columns={AUDIT_COLUMNS}
      getRowKey={(row) => row.id}
      isLoading={isLoading}
      isFetching={isFetching}
      error={error}
      isOverrunPage={isOverrunPage}
      onPageChange={onPageChange}
      onPageSizeChange={onPageSizeChange}
      emptyMessage={emptyMessage}
      {...(emptyDetail === undefined ? {} : { emptyDetail })}
      {...(emptyAction === undefined ? {} : { emptyAction })}
      ariaLabel="Audit log entries"
    />
  );
}

/** Monospace, because these are identifiers to be compared character by character, not prose. */
const MONO = { fontFamily: 'monospace' } as const;

const AUDIT_COLUMNS: readonly Column<AuditEntry>[] = [
  {
    key: 'occurredAt',
    // "(exact)" is in the header from the section 3.1 sketch, and it earns its place: it tells the
    // reader up front that this column is not rounded and not relative.
    header: 'Occurred (exact)',
    /**
     * THE ROW'S LINK, AND AN ANCHOR RATHER THAN AN onClick (section 3.1 rule H): middle-click,
     * Ctrl-click and "Open in new tab" all work, which is how an investigator compares two entries
     * side by side. A div with a click handler supports none of that and is invisible to a keyboard.
     *
     * IT IS ON THIS CELL AND NOT ON THE WHOLE ROW because PaginatedTable exposes no row-link or
     * onRowClick prop and this slice may not edit shared/. Reported as a Phase 0 gap: a
     * `rowHref?: (row: T) => string` on PaginatedTable would let the entire row be one anchor
     * without any slice composing its own table.
     *
     * EXACT DATE, TIME AND SECONDS, NEVER RELATIVE (section 6 rule G). Seconds are load-bearing: the
     * server's tie-break on id exists because one request writes several entries in the same second.
     */
    render: (row) => (
      <Link
        component={RouterLink}
        to={`/audit/${row.id}`}
        sx={{ whiteSpace: 'nowrap' }}
      >
        {formatOccurredAt(row.occurredAt)}
      </Link>
    ),
    width: '1%',
  },
  {
    key: 'actorUserId',
    header: 'Actor',
    /**
     * SHORTENED FOR THE COLUMN, FULL IN THE title. There is no name to show and no lookup to make;
     * the id is the identity. The ellipsis is the UI's own shortening -- distinct from the server's
     * write-time truncation at 100 characters (AuditApi.cs:46), which is rendered verbatim.
     */
    render: (row) => (
      <Box component="span" title={row.actorUserId} sx={MONO}>
        {middleTruncate(row.actorUserId)}
      </Box>
    ),
  },
  {
    key: 'actorRole',
    header: 'Role',
    /**
     * THE ROLE AT THE TIME OF THE ACTION, not the actor's role now (section 6 rule B). It is a
     * STRING here while `role` is an integer everywhere else in the API (AuditApi.cs:35), and it can
     * be the literal "Unknown" for an unauthenticated attempt (:22) -- which is rendered verbatim,
     * because a role this UI does not recognise is itself information.
     */
    render: (row) => auditRoleLabel(row.actorRole),
  },
  {
    key: 'action',
    header: 'Action',
    /**
     * THE SERVER'S CODE, VERBATIM AND UNPRETTIFIED (section 6 rule A). "Customer.Create" is what the
     * reader types into the Action filter, what appears in a bug report and what AuditActions.cs
     * declares; de-camel-casing it to "Customer create" makes the table and the filter disagree, and
     * two codes can prettify to the same words.
     */
    render: (row) => (
      <Typography component="span" variant="body2" sx={MONO}>
        {dashIfEmpty(row.action)}
      </Typography>
    ),
  },
  {
    key: 'outcome',
    header: 'Outcome',
    /**
     * THE FOURTH STATUS VOCABULARY (Success | Denied | Failure), through the shared StatusChip so
     * Denied is amber here and on every other screen -- and always with its WORD showing, never a
     * bare colour (section 8.4).
     *
     * A Denied row is a NORMAL, EXPECTED entry: PermissionChecker.RequireAsync logs one on every
     * refused attempt. Styling it as an error would make routine permission checks look like
     * incidents.
     */
    render: (row) => <StatusChip status={row.outcome} />,
    width: '1%',
  },
];
