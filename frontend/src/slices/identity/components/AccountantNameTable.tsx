import type { PaginatedResponse } from '../../../shared/api/paginated';
import { PaginatedTable, type Column } from '../../../shared/components/PaginatedTable';
import type { AccountantSummary } from '../types';

/**
 * What an AccountantUser sees: ONE COLUMN. No row menu, no status column, no email column and no
 * em-dash placeholders standing in for withheld fields -- the same pager as the Admin table, because
 * the envelope is identical in both responses.
 *
 * IT TAKES PaginatedResponse<AccountantSummary>, the shape ListAccountantsHandler.cs:88-95 actually
 * returns to this caller. The five detail keys are ABSENT FROM THE JSON, not null, so there is nothing
 * to render defensively and nothing here inspects a row for the presence of a field: the narrowing
 * happened once, in AccountantListScreen, against `session.role` -- the same discriminator the server
 * branches on at :77.
 *
 * WHY THIS ROLE SEES THE LIST AT ALL: 02-AuthorizationMatrix.md section 2 -- assigning a ticket
 * requires knowing who exists, and that needs a name, not a login history. So this component is not a
 * degraded Admin table; it is the whole of what this role is entitled to, and the screen's mandatory
 * subtitle says so in words.
 *
 * There is no `emptyAction`: an AccountantUser cannot invite anybody
 * (IdentityActionCatalogue.cs:25 -- AccountantAdmin only), so offering the button would draw a control
 * whose only outcome is a 403.
 */
export function AccountantNameTable({
  data,
  isLoading,
  isFetching,
  error,
  isOverrunPage,
  onPageChange,
  onPageSizeChange,
}: {
  data: PaginatedResponse<AccountantSummary> | undefined;
  isLoading: boolean;
  isFetching: boolean;
  error: unknown;
  isOverrunPage: boolean;
  /** Already 1-based, straight from PaginatedTable. */
  onPageChange: (pageNumber: number) => void;
  onPageSizeChange: (pageSize: number) => void;
}) {
  const columns: readonly Column<AccountantSummary>[] = [
    { key: 'displayName', header: 'Name', render: (row) => row.displayName },
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
      emptyMessage="No Accountants to show."
      emptyDetail="At least one Accountant Admin always exists, so this is unexpected. Reload, and report it if it persists."
    />
  );
}
