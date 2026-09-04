import { useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Divider from '@mui/material/Divider';
import IconButton from '@mui/material/IconButton';
import Link from '@mui/material/Link';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import Snackbar from '@mui/material/Snackbar';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Typography from '@mui/material/Typography';
import MoreVertIcon from '@mui/icons-material/MoreVert';
import { DEFAULT_PAGE_SIZE } from '../../../shared/api/paginated';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { PageHeader } from '../../../shared/components/PageHeader';
import { PaginatedTable, type Column } from '../../../shared/components/PaginatedTable';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { UserRole } from '../../../shared/format/enums';
import { can } from '../../../shared/permissions/can';
import { TicketTypeStatusChip } from '../components/TicketTypeStatusChip';
import { ToggleTicketTypeDialog } from '../components/ToggleTicketTypeDialog';
import { useTicketTypeList, useToggleTicketType } from '../queries';
import type { TicketTypeListItem } from '../types';

/**
 * The one list in the application every role can reach. Screens/TicketTypesScreens.md section 3.
 *
 * PaginatedTable, never a hand-rolled Table + TablePagination, and @mui/x-data-grid is banned
 * (GeneralUIArchitecture.md section 8.2). The pager is rendered from response.pageSize inside that
 * component, because PaginatedQuery.Normalize CLAMPS pageSize to 50 rather than rejecting it.
 *
 * FIVE COLUMNS AND NO SIXTH. TicketTypeListItemDto carries six properties and no others -- there is
 * no description, createdAt, updatedAt or field count on it, and a column for any of them means an
 * N+1 of /detail calls behind a table (section 3.1 rule A).
 *
 * NO CLIENT-SIDE SORT, SEARCH, GROUPING OR CATEGORY FILTER. /list accepts three query parameters and
 * no search term or sort key; the server orders by DisplayName, Id and pages AFTER ordering
 * (ListTicketTypesHandler.cs:41), so sorting one page sorts fifteen rows out of two hundred and
 * presents the result as if it were the whole ordering. Category is a text column, not a grouping:
 * a client-side regroup of one page produces headings that appear and disappear as the user pages
 * (section 3.1 rule B).
 *
 * NO DELETE, DUPLICATE, IMPORT, EXPORT OR BULK TOGGLE. 02-AuthorizationMatrix.md section 5 grants
 * delete to nobody and there is no endpoint. A Delete button that calls nothing is a support ticket;
 * one that calls toggle is a lie about what happened.
 */

/** *All* omits the parameter entirely. See ACTIVE_FILTERS below. */
type ActiveFilter = 'all' | 'active' | 'inactive';

/**
 * THE THREE-STATE FILTER, AND WHY IT CANNOT BE A CHECKBOX.
 * ListTicketTypesHandler.cs:29-38 applies `t.IsActive == req.ActiveOnly.Value` and only when
 * HasValue, so the parameter is an EQUALITY and not a relaxation:
 *
 *   omitted -> active AND inactive      true -> active only      false -> INACTIVE ONLY
 *
 * A two-state "Active only" checkbox bound straight to the parameter sends `false` when unticked and
 * shows an Accountant nothing but deactivated types -- indistinguishable on screen from a catalogue
 * that failed to load, on a screen whose empty state also says the catalogue is empty. Punch-list
 * item 20; the three-option control is the mandatory workaround.
 */
const ACTIVE_FILTERS: readonly { value: ActiveFilter; label: string; activeOnly?: boolean }[] = [
  { value: 'all', label: 'All' },
  { value: 'active', label: 'Active', activeOnly: true },
  { value: 'inactive', label: 'Inactive', activeOnly: false },
];

function activeOnlyFor(filter: ActiveFilter): boolean | undefined {
  return ACTIVE_FILTERS.find((entry) => entry.value === filter)?.activeOnly;
}

export function TicketTypeListScreen() {
  const { role } = useAuthenticatedSession();

  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);
  const [filter, setFilter] = useState<ActiveFilter>('all');

  const [menuRow, setMenuRow] = useState<TicketTypeListItem | null>(null);
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null);
  const [pendingDeactivation, setPendingDeactivation] = useState<TicketTypeListItem | null>(null);
  const [snackbar, setSnackbar] = useState<string | null>(null);

  const list = useTicketTypeList({ pageNumber, pageSize, activeOnly: activeOnlyFor(filter) });
  const toggle = useToggleTicketType();

  const canCreate = can(role, 'CreateTicketType');
  const canEdit = can(role, 'EditTicketType');
  const canToggle = can(role, 'ToggleTicketType');

  /**
   * The filter is HIDDEN for CustomerAdmin and Employee rather than disabled: the server never reads
   * activeOnly for them -- it sits in the `else` branch of ListTicketTypesHandler.cs:29-38 -- so a
   * visible control that demonstrably does nothing is worse than no control (section 6.2 rule C).
   * This mirrors the handler's own IsCustomerSide test; it hides a CONTROL, never a row.
   */
  const isCustomerSide = role === UserRole.CustomerAdmin || role === UserRole.Employee;

  const closeMenu = () => {
    setMenuAnchor(null);
    setMenuRow(null);
  };

  const newTicketTypeButton = canCreate ? (
    <Button component={RouterLink} to="/ticket-types/new" variant="contained">
      New ticket type
    </Button>
  ) : undefined;

  const columns: readonly Column<TicketTypeListItem>[] = [
    {
      key: 'displayName',
      header: 'Display name',
      render: (row) => (
        <Link component={RouterLink} to={`/ticket-types/${row.id}`}>
          {row.displayName}
        </Link>
      ),
    },
    {
      key: 'code',
      header: 'Code',
      // Immutable, so it is the stable human handle. Monospaced from the theme's font stack, never a
      // hex or a font-family literal.
      render: (row) => (
        <Typography variant="body2" component="span" sx={{ fontFamily: 'monospace' }}>
          {row.code}
        </Typography>
      ),
    },
    { key: 'category', header: 'Category', render: (row) => row.category },
    {
      key: 'isActive',
      header: 'Status',
      // Never the raw boolean. See TicketTypeStatusChip for why this is not shared/StatusChip.
      render: (row) => <TicketTypeStatusChip isActive={row.isActive} />,
    },
    {
      key: 'currentVersionNumber',
      header: 'Version',
      align: 'right',
      // "v3", not "3" -- a bare integer in a column next to a version count reads as a quantity.
      render: (row) => `v${String(row.currentVersionNumber)}`,
    },
  ];

  /**
   * The row overflow menu is a column only when the role has at least one row action. An empty
   * three-dot button on every row of a Customer Admin's list is an affordance that opens nothing.
   */
  const actionColumns: readonly Column<TicketTypeListItem>[] =
    canEdit || canToggle
      ? [
          {
            key: 'actions',
            header: '',
            align: 'right',
            width: 56,
            render: (row) => (
              <IconButton
                // Icon-only buttons carry an aria-label, and it names the row (section 8.4 item 4).
                aria-label={`Actions for ${row.displayName}`}
                size="small"
                onClick={(event) => {
                  setMenuAnchor(event.currentTarget);
                  setMenuRow(row);
                }}
              >
                <MoreVertIcon fontSize="small" />
              </IconButton>
            ),
          },
        ]
      : [];

  return (
    <>
      <PageHeader
        title="Ticket types"
        subtitle="The templates a ticket of each kind is generated from."
        {...(newTicketTypeButton === undefined ? {} : { action: newTicketTypeButton })}
      />

      {!isCustomerSide && (
        <Box sx={{ mb: 2 }}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={filter}
            aria-label="Filter by status"
            onChange={(_event, next: ActiveFilter | null) => {
              // `exclusive` yields null when the pressed button is already selected. Keeping the
              // current value is right: there is no fourth "nothing selected" state to fall into.
              if (next === null) return;
              setFilter(next);
              setPageNumber(1);
            }}
          >
            {ACTIVE_FILTERS.map((entry) => (
              <ToggleButton key={entry.value} value={entry.value}>
                {entry.label}
              </ToggleButton>
            ))}
          </ToggleButtonGroup>
        </Box>
      )}

      {/* A row action that failed renders ABOVE the table (section 7.2). The query's own error goes
          inside PaginatedTable, which replaces the rows and keeps the header and pager. */}
      <ErrorBanner error={pendingDeactivation === null ? toggle.error : null} />

      <PaginatedTable<TicketTypeListItem>
        data={list.data}
        columns={[...columns, ...actionColumns]}
        getRowKey={(row) => row.id}
        isLoading={list.isLoading}
        isFetching={list.isFetching}
        error={list.error}
        onPageChange={setPageNumber}
        onPageSizeChange={(next) => {
          setPageSize(next);
          setPageNumber(1);
        }}
        isOverrunPage={list.isOverrunPage}
        ariaLabel="Ticket types"
        // Empty is not an error, and the sentence is written for THIS list (section 7.4). The action
        // is offered only where the role can act on it; a Customer Admin gets the sentence alone.
        emptyMessage={emptyMessageFor(filter)}
        {...(newTicketTypeButton === undefined ? {} : { emptyAction: newTicketTypeButton })}
      />

      <Menu anchorEl={menuAnchor} open={menuAnchor !== null} onClose={closeMenu}>
        {canEdit && menuRow !== null && (
          <MenuItem component={RouterLink} to={`/ticket-types/${menuRow.id}/edit`}>
            Edit
          </MenuItem>
        )}
        {canEdit && canToggle && <Divider />}
        {canToggle && menuRow !== null && (
          <MenuItem
            onClick={() => {
              const row = menuRow;
              closeMenu();
              if (row.isActive) {
                // Deactivation is confirmed, because it is invisible from the other side of the
                // Customer boundary.
                setPendingDeactivation(row);
                return;
              }
              // Reactivation needs no confirmation: it only makes something visible again.
              toggle.mutate(
                { ticketTypeId: row.id, newIsActive: true },
                {
                  // Rendered from the RETURNED isActive, never from what was sent: toggle is
                  // idempotent and writes nothing when the state already holds
                  // (ToggleTicketTypeHandler.cs:44-45), so a 200 is not evidence anything moved.
                  onSuccess: (updated) => {
                    setSnackbar(
                      updated.isActive
                        ? `${updated.displayName} is active.`
                        : `${updated.displayName} is inactive.`,
                    );
                  },
                },
              );
            }}
          >
            {menuRow.isActive ? 'Deactivate' : 'Reactivate'}
          </MenuItem>
        )}
      </Menu>

      <ToggleTicketTypeDialog
        open={pendingDeactivation !== null}
        displayName={pendingDeactivation?.displayName ?? ''}
        code={pendingDeactivation?.code ?? ''}
        isPending={toggle.isPending}
        error={toggle.error}
        onClose={() => {
          setPendingDeactivation(null);
          toggle.reset();
        }}
        onConfirm={() => {
          if (pendingDeactivation === null) return;
          toggle.mutate(
            { ticketTypeId: pendingDeactivation.id, newIsActive: false },
            {
              onSuccess: (updated) => {
                setPendingDeactivation(null);
                setSnackbar(
                  updated.isActive
                    ? `${updated.displayName} is active.`
                    : `${updated.displayName} is inactive.`,
                );
              },
            },
          );
        }}
      />

      {/* Successes are the only toasts in the application (section 7.2). */}
      <Snackbar
        open={snackbar !== null}
        message={snackbar ?? ''}
        autoHideDuration={4000}
        onClose={() => setSnackbar(null)}
      />
    </>
  );
}

/**
 * The empty sentence names the filter, because "No ticket types yet" under *Inactive* is wrong: the
 * catalogue may be full of active ones.
 */
function emptyMessageFor(filter: ActiveFilter): string {
  if (filter === 'active') return 'No active ticket types.';
  if (filter === 'inactive') return 'No deactivated ticket types.';
  return 'No ticket types yet.';
}
