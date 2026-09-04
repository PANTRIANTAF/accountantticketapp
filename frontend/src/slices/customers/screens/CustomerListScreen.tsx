import { useEffect, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import AddIcon from '@mui/icons-material/Add';
import Button from '@mui/material/Button';
import Link from '@mui/material/Link';
import MenuItem from '@mui/material/MenuItem';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import Typography from '@mui/material/Typography';
import { PageHeader } from '../../../shared/components/PageHeader';
import { PaginatedTable, type Column } from '../../../shared/components/PaginatedTable';
import { StatusChip } from '../../../shared/components/StatusChip';
import { DEFAULT_PAGE_SIZE } from '../../../shared/api/paginated';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { can } from '../../../shared/permissions/can';
import { useCustomerList } from '../queries';
import type { CustomerStatus, CustomerSummary } from '../types';

/**
 * Route /customers, inside the shell, RequireRole'd to AccountantAdmin and AccountantUser
 * (GeneralUIArchitecture.md section 4.1). It is the landing screen for both Accountant roles, which
 * is why every failure mode below matters more here than anywhere else in the slice.
 *
 * THREE COLUMNS AND NO FOURTH: legal name, trading name, status. CustomerSummaryDto has exactly four
 * keys and one of them is the id (CustomerSummaryDto.cs:5-8). Contact email, city, employee count,
 * ticket count and onboarded date are not in it, and resolving one per row is fifteen extra requests
 * per page.
 *
 * NO COLUMN SORT, NO EXPORT, NO BULK ACTION, NO ROW-LEVEL SUSPEND.
 * ListCustomersRequestDto has no sort parameter and ListCustomersHandler.cs:56-57 orders by legalName
 * then id, so a clickable header could only reorder the fifteen rows on screen out of a hundred and
 * would look like corrupt data. And a row menu offering Suspend two pixels from Open is how an entire
 * company's staff loses its logins by mis-click -- status changes live on the detail screen, behind a
 * dialog that has room for the four consequences (CustomersScreens.md section 3.3).
 *
 * NO CLIENT-SIDE .filter() OVER THE ROWS, EVER. CustomerScope.cs:37-41 filters server-side on the
 * primary key -- the Customer IS the tenant boundary -- and 02-AuthorizationMatrix.md:311 requires
 * out-of-scope records to be "absent from the API response, not merely unrendered". A
 * `.filter(c => c.id === session.customerId)` here would not be a safeguard: it would be evidence of
 * a server-side leak being concealed, and it would break the pager, because totalCount counts the
 * rows it discards.
 */

/** ListCustomersHandler.cs:46-47 answers 422 "Search must be at most 200 characters." above this. */
const SEARCH_MAX_LENGTH = 200;

/**
 * Every keystroke is a new query key (section 3.1), so an undebounced box is one POST per character.
 * 300ms is CustomersScreens.md section 3.5 rule C.
 */
const SEARCH_DEBOUNCE_MS = 300;

export function CustomerListScreen() {
  const { role } = useAuthenticatedSession();

  /**
   * GATED ON `OnboardCustomer`, NOT `CreateCustomer`. Both are [AccountantAdmin] today, so the wrong
   * one gives the right answer -- and becomes a lie the moment either changes independently. The
   * button navigates to /customers/new, which posts to /api/customers/onboard, and
   * OnboardCustomerHandler.cs:59 checks "OnboardCustomer" (EmployeesActionCatalogue.cs:22). Gate on
   * the action the endpoint the button actually reaches checks.
   */
  const canAddCustomer = can(role, 'OnboardCustomer');

  // What the user is typing, and what the query key is allowed to see 300ms later. Two pieces of
  // state on purpose: one keystroke must not be one request.
  const [searchInput, setSearchInput] = useState('');
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<CustomerStatus | null>(null);
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = useState<number>(DEFAULT_PAGE_SIZE);

  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(searchInput);
    }, SEARCH_DEBOUNCE_MS);

    return () => {
      clearTimeout(timer);
    };
  }, [searchInput]);

  /**
   * RESET TO PAGE 1 WHENEVER A FILTER CHANGES (CustomersScreens.md section 3.5 rule E). Without it a
   * narrowed filter leaves the pager on page 4 of a one-page result, and the user is shown the
   * over-run empty state instead of their rows -- which reads as missing data, not as a paging
   * artefact.
   */
  const filtersAreSet = search !== '' || status !== null;

  const changeSearch = (value: string) => {
    setSearchInput(value);
    setPageNumber(1);
  };

  const changeStatus = (value: CustomerStatus | null) => {
    setStatus(value);
    setPageNumber(1);
  };

  const clearFilters = () => {
    setSearchInput('');
    setSearch('');
    setStatus(null);
    setPageNumber(1);
  };

  /**
   * `search: null` and `status: null` mean "no filter". NEVER '' -- ListCustomersHandler.cs:31-33
   * trims and compares case-sensitively against the two constants, so "" and "active" are both
   * 422 "Unknown customer status." (section 12.2 rule A).
   */
  const query = useCustomerList({
    status,
    search: search === '' ? null : search,
    pageNumber,
    pageSize,
  });

  const columns: readonly Column<CustomerSummary>[] = [
    {
      key: 'legalName',
      header: 'Legal name',
      // THE ROW'S LINK TO THE DETAIL SCREEN. PaginatedTable takes no onRowClick, and a real anchor is
      // better than one: middle-click, Ctrl-click and "copy link" all work, and a keyboard user
      // reaches it by Tab rather than by guessing that a table row is activatable.
      render: (row) => (
        <Link component={RouterLink} to={`/customers/${row.id}`} underline="hover">
          {row.legalName}
        </Link>
      ),
    },
    {
      key: 'tradingName',
      header: 'Trading name',
      // An em dash for an absent optional, matching the layout in CustomersScreens.md section 3.1.
      // Never '' -- a blank cell reads as data that failed to load.
      render: (row) => row.tradingName ?? '—',
    },
    {
      key: 'status',
      header: 'Status',
      // Typed CustomerStatus all the way from types.ts, so TypeScript refuses an 'Invited' chip on a
      // Customer (section 12.2 rule B). Invited is a UserAccount status: a newly onboarded Customer
      // is Active while its first Customer Admin is Invited -- two rows, two vocabularies.
      render: (row) => <StatusChip status={row.status} size="small" />,
    },
  ];

  return (
    <Stack spacing={3}>
      <PageHeader
        title="Customers"
        action={
          canAddCustomer ? (
            <Button
              component={RouterLink}
              to="/customers/new"
              variant="contained"
              startIcon={<AddIcon />}
            >
              Add Customer
            </Button>
          ) : undefined
        }
      />

      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ alignItems: 'flex-start' }}>
        {/*
          THE LABEL SAYS WHAT THE SEARCH SEARCHES. ListCustomersHandler.cs:48-52 runs ILIKE over
          legalName OR tradingName and nothing else -- not tax number, not city, not contact email. A
          box labelled just "Search" that silently ignores a pasted tax number reads as missing data.

          `%` and `_` are NOT stripped: ListCustomersHandler.cs:73-76 escapes them server-side, and
          stripping them here would quietly change what the user typed.
        */}
        <TextField
          label="Search legal or trading name"
          value={searchInput}
          onChange={(event) => {
            changeSearch(event.target.value);
          }}
          slotProps={{ htmlInput: { maxLength: SEARCH_MAX_LENGTH } }}
          helperText={`Legal name or trading name. At most ${String(SEARCH_MAX_LENGTH)} characters.`}
          sx={{ minWidth: 280 }}
        />

        {/*
          EXACTLY TWO OPTIONS PLUS "All", AND "All" SENDS NO STATUS AT ALL. There is no Invited
          option: Customers.Core.CustomerStatus declares two values and migration
          20260901_002_AddCustomerStatusCheck.sql enforces them with a CHECK constraint. Offering a
          third would be a 422 on selection, which reads as a server bug.
        */}
        <TextField
          select
          label="Status"
          value={status ?? ''}
          onChange={(event) => {
            const value = event.target.value;
            changeStatus(value === '' ? null : (value as CustomerStatus));
          }}
          sx={{ minWidth: 180 }}
        >
          <MenuItem value="">All</MenuItem>
          <MenuItem value="Active">Active</MenuItem>
          <MenuItem value="Suspended">Suspended</MenuItem>
        </TextField>
      </Stack>

      {/*
        EVERY STATE OF CustomersScreens.md section 3.4 IS HANDLED INSIDE PaginatedTable: skeleton rows
        on first load with the header and pager staying put, a progress bar on a refetch with the rows
        left on screen, an ErrorBanner replacing the body on failure, and EmptyState otherwise.

        THE EMPTY COPY BRANCHES ON WHETHER FILTERS ARE SET, and getting that wrong is failure mode 3
        of section 5.1: "No customers match these filters" on a Customer that exists is a report of
        missing data. The over-run case -- items: [] with totalCount > 0, a 200 not a 404 -- is
        `isOverrunPage`, which EmptyState turns into "back to the first page" instead of "no results".

        The pager is rendered from response.pageSize, never from the value requested:
        PaginatedQuery.cs clamps to 50 with a 200 rather than rejecting, so a requested 999 must not
        become the pager's row count. PaginatedTable does that in one place for the whole SPA, and its
        page-size options stop at MAX_PAGE_SIZE.
      */}
      <PaginatedTable<CustomerSummary>
        data={query.data}
        columns={columns}
        getRowKey={(row) => row.id}
        isLoading={query.isLoading}
        isFetching={query.isFetching}
        error={query.error}
        isOverrunPage={query.isOverrunPage}
        onPageChange={setPageNumber}
        onPageSizeChange={(size) => {
          setPageSize(size);
          setPageNumber(1);
        }}
        ariaLabel="Customers"
        emptyMessage={filtersAreSet ? 'No customers match these filters' : 'No customers yet'}
        emptyAction={
          filtersAreSet ? (
            <Button onClick={clearFilters}>Clear filters</Button>
          ) : canAddCustomer ? (
            // For an AccountantUser this slot is empty and the sentence stands alone
            // (CustomersScreens.md section 3.4): they cannot onboard a Customer, so an Add Customer
            // button here would be a 403 waiting to happen.
            <Button
              component={RouterLink}
              to="/customers/new"
              variant="contained"
              startIcon={<AddIcon />}
            >
              Add Customer
            </Button>
          ) : undefined
        }
      />

      {/*
        Sorted by legal name, server-side, with no client say in it. Stated rather than implied,
        because the absence of sortable headers is a deliberate decision and not an omission.
      */}
      <Typography variant="caption" color="text.secondary">
        Sorted by legal name.
      </Typography>
    </Stack>
  );
}
