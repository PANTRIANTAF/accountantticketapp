import Box from '@mui/material/Box';
import Typography from '@mui/material/Typography';
import type { PaginatedResponse } from '../../../shared/api/paginated';
import { PaginatedTable, type Column } from '../../../shared/components/PaginatedTable';
import { StatusChip } from '../../../shared/components/StatusChip';
import { formatDateTime } from '../../../shared/format/dates';
import { ROLE_LABELS, type UserRole } from '../../../shared/format/enums';
import type { AccountantDetail } from '../types';
import { AccountantRowMenu, isSameAccount } from './AccountantRowMenu';

/**
 * What an AccountantAdmin sees: six columns and the row menu. It receives a page and callbacks and
 * FETCHES NOTHING.
 *
 * IT IS A SEPARATE COMPONENT FROM AccountantNameTable ON PURPOSE, and that is normative rather than
 * stylistic (IdentityScreens.md section 2 rule C, 02-AuthorizationMatrix.md section 12 rule 2,
 * GeneralUIArchitecture.md section 6.2 rule A). One table rendering `row.status ?? '—'` would show an
 * AccountantUser an em-dash column, telling them a field exists and is being withheld. The narrow view
 * is not a filtered wide view: if the UI is filtering for security, the server has already leaked it.
 *
 * A. NOTHING IS RENDERED RAW. `role` goes through ROLE_LABELS -- a cell reading `0` has leaked the wire
 *    format and a cell reading "Admin" has broken 00-Glossary.md, which bans the bare word because it
 *    is ambiguous between AccountantAdmin and CustomerAdmin. `status` goes through StatusChip, which
 *    owns the one colour map so Suspended is never green here and amber elsewhere. And `status` is a
 *    STRING while `role` is a NUMBER in the same row, with nothing in the JSON marking the difference,
 *    so never Number(row.status) and never String(row.role).
 * B. BOTH TIMESTAMPS CARRY AN OFFSET (section 10.2) and go through shared/format/dates.ts.
 *    `lastLoginAt` is null for anyone who has never signed in -- an em dash, never "Invalid Date".
 * C. `(you)` IS APPENDED TO THE CALLER'S OWN ROW, matched case-insensitively. The row menu is absent
 *    there, and this label is the only thing that explains why.
 * D. Rendered THROUGH PaginatedTable. Table + TablePagination is never assembled in a slice
 *    (section 8.2) and @mui/x-data-grid is banned.
 * E. NO SORT HEADERS, NO SEARCH BOX AND NO STATUS FILTER. ListAccountantsHandler.cs:58-61 applies no
 *    status filter on purpose -- "an Admin cannot reactivate somebody the list does not show" -- and
 *    :69-70 orders by DisplayName then Id, accepting neither a sort nor a search parameter. A header
 *    that sorts the current page is a lie about a server-paginated list, and an "active only" toggle
 *    would remove the only route to reactivation.
 */
export function AccountantAdminTable({
  data,
  isLoading,
  isFetching,
  error,
  isOverrunPage,
  onPageChange,
  onPageSizeChange,
  role,
  currentUserId,
  onSuspend,
  onReactivate,
  onPromote,
  onDemote,
}: {
  data: PaginatedResponse<AccountantDetail> | undefined;
  isLoading: boolean;
  isFetching: boolean;
  error: unknown;
  isOverrunPage: boolean;
  /** Already 1-based, straight from PaginatedTable. Nothing here converts anything. */
  onPageChange: (pageNumber: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  /** The caller's role, for can() inside the row menu. */
  role: UserRole;
  currentUserId: string;
  onSuspend: (row: AccountantDetail) => void;
  onReactivate: (row: AccountantDetail) => void;
  onPromote: (row: AccountantDetail) => void;
  onDemote: (row: AccountantDetail) => void;
}) {
  const columns: readonly Column<AccountantDetail>[] = [
    {
      key: 'displayName',
      header: 'Name',
      render: (row) => (
        <>
          {row.displayName}
          {isSameAccount(row.id, currentUserId) && (
            // Rule C. A Typography rather than a Chip: it is an annotation on the name, not a status.
            <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>
              (you)
            </Typography>
          )}
        </>
      ),
    },
    { key: 'loginEmail', header: 'Email', render: (row) => row.loginEmail },
    // Rule A. ROLE_LABELS is total over UserRole, so there is no fallback string to get wrong.
    { key: 'role', header: 'Role', render: (row) => ROLE_LABELS[row.role] },
    // Rule A. `status` is typed AccountantStatus, so TypeScript refuses a Customer's or an Employee's
    // vocabulary here even though StatusChip shares one colour map across all four.
    { key: 'status', header: 'Status', render: (row) => <StatusChip status={row.status} /> },
    // Rule B. Non-null in the DTO: an account cannot exist without having been created.
    { key: 'createdAt', header: 'Created', render: (row) => formatDateTime(row.createdAt) },
    {
      key: 'lastLoginAt',
      header: 'Last sign-in',
      // Rule B. An em dash for "never signed in", which is a real and common state for an Invited
      // account -- not an error and not a missing value to apologise for.
      render: (row) => (row.lastLoginAt === null ? '—' : formatDateTime(row.lastLoginAt)),
    },
    {
      key: 'actions',
      // A real header rather than an empty cell: a blank <th> is announced as nothing at all.
      header: 'Actions',
      align: 'right',
      width: 96,
      render: (row) => (
        <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
          <AccountantRowMenu
            row={row}
            role={role}
            isOwnRow={isSameAccount(row.id, currentUserId)}
            onSuspend={() => onSuspend(row)}
            onReactivate={() => onReactivate(row)}
            onPromote={() => onPromote(row)}
            onDemote={() => onDemote(row)}
          />
        </Box>
      ),
    },
  ];

  return (
    <PaginatedTable
      data={data}
      columns={columns}
      getRowKey={(row) => row.id}
      isLoading={isLoading}
      isFetching={isFetching}
      error={error}
      onPageChange={onPageChange}
      onPageSizeChange={onPageSizeChange}
      isOverrunPage={isOverrunPage}
      ariaLabel="Accountants"
      // `items: []` with `totalCount: 0` CANNOT HAPPEN: 02-AuthorizationMatrix.md section 2 and
      // AccountInvariants.RequireAnActiveAdminRemainsAsync together guarantee an Active Accountant
      // Admin always exists, and the caller is looking at this table. So the copy says so instead of
      // designing a state for it -- "No accountants yet" plus an Invite button would be a fiction.
      // The over-run page is a different thing and PaginatedTable handles it from isOverrunPage.
      emptyMessage="No Accountants to show."
      emptyDetail="At least one Accountant Admin always exists, so this is unexpected. Reload, and report it if it persists."
    />
  );
}
