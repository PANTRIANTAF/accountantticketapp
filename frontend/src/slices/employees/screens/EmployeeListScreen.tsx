import { useEffect, useState } from 'react';
import { Link as RouterLink, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import Autocomplete from '@mui/material/Autocomplete';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Link from '@mui/material/Link';
import Menu from '@mui/material/Menu';
import MenuItem from '@mui/material/MenuItem';
import MenuList from '@mui/material/MenuList';
import Paper from '@mui/material/Paper';
import Snackbar from '@mui/material/Snackbar';
import Stack from '@mui/material/Stack';
import TextField from '@mui/material/TextField';
import MoreVertIcon from '@mui/icons-material/MoreVert';
import { EmptyState } from '../../../shared/components/EmptyState';
import { PageHeader } from '../../../shared/components/PageHeader';
import { PaginatedTable, type Column } from '../../../shared/components/PaginatedTable';
import { StatusChip } from '../../../shared/components/StatusChip';
import { useAuthenticatedSession } from '../../../shared/auth/useSession';
import { ROLE_LABELS, UserRole, type EmployeeStatus } from '../../../shared/format/enums';
import { can } from '../../../shared/permissions/can';
import { DEFAULT_PAGE_SIZE } from '../../../shared/api/paginated';
import { listCustomers } from '../../customers/api';
import type { CustomerSummary } from '../../customers/types';
import { useEmployeeList } from '../queries';
import { SEARCH_TERM_MAX_LENGTH } from '../schemas';
import { InviteEmployeeDialog } from '../components/InviteEmployeeDialog';
import { RegisterEmployeeDialog } from '../components/RegisterEmployeeDialog';
import type { EmployeeDetail, EmployeeSummary } from '../types';

/**
 * `/employees` -- the Employee list. AA, AU and CA; an `Employee` gets `AccessDeniedPage` from
 * `RequireRole` in the route table, not a redirect (GeneralUIArchitecture.md section 4.3 rule A).
 *
 * A. NEVER FILTER ROWS IN THE BROWSER. `CustomerScope` does it server-side on every query a
 *    Customer-side caller can reach. `.filter(e => e.customerId === session.customerId)` is not a
 *    safeguard: it is evidence of a server-side leak being CONCEALED, and it breaks the pager too,
 *    because `totalCount` counts the rows it throws away. `EmployeeSummary` has no `customerId` at
 *    all -- the shape of the API telling you not to (EmployeesScreens.md section 9 rule A).
 *
 * B. NO DEFAULT STATUS FILTER. The endpoint returns Active AND Departed unless filtered, and
 *    ListEmployeesHandler notes that a default which hides Departed rows "makes a Customer Admin think
 *    the record is gone" -- while nothing ever deletes an Employee (02-AuthorizationMatrix.md
 *    section 4: "Delete an Employee record — Nobody.").
 *
 * C. THE CUSTOMER FILTER IS A ROLE CHECK, NOT `can()`, and exists for the Accountant roles only.
 *    ListEmployeesHandler.cs:47-53 answers 403 "You may only list employees at your own customer."
 *    when a CustomerAdmin names another Customer, deliberately. And because `EmployeeSummary` carries
 *    no employer name, an Accountant with no Customer chosen would see a page of names belonging to
 *    unidentified Customers -- so that state is an `EmptyState` asking them to pick one, and the query
 *    does not run (section 4.3 callout; BACKEND_CHANGES_REQUIRED, "add customerId to the summary").
 *
 * D. SEARCH IS DEBOUNCED 300 ms AND CAPPED AT 200 CHARACTERS. Every keystroke is a new query key, so
 *    an undebounced box is one POST per character, and 201 characters is
 *    422 "Search must be at most 200 characters." The cap is enforced on input rather than waited for.
 *    The helper text says the search also matches WORK EMAIL -- a column this table cannot show,
 *    because the summary DTO has no email, so without that sentence a Customer Admin reports the
 *    search as broken when it returns a row whose visible fields do not contain what they typed.
 *
 * E. EVERY FILTER CHANGE RESETS `pageNumber` TO 1, or a narrowed filter leaves the pager on page 4 of
 *    a one-page result and the user sees the over-run empty state instead of their rows.
 *
 * F. NO COLUMN SORTING. `ListEmployeesRequestDto` has no sort parameter; the order is fixed
 *    server-side to family name, given name, id, to match `idx_employees_customer_name`. A
 *    client-side sort would reorder one page of fifteen out of a hundred and look like a bug.
 *
 * G. THE ROW MENU OFFERS *View* AND NOTHING ELSE, AND THAT IS A DELIBERATE DEPARTURE FROM
 *    EmployeesScreens.md SECTION 4.3, whose row-menu row also lists Edit, Invite, Change role,
 *    Suspend access, Restore access and Mark departed. Those cannot be drawn correctly from this DTO:
 *    section 5.5 rule C forbids opening the edit dialog before the detail has resolved (a partial
 *    pre-fill erases the fields it did not load); section 8.6 requires *Invite* to be blocked when
 *    `workEmail` is absent, and the summary has no `workEmail`; and section 8.5 rule C requires
 *    *Suspend access* / *Restore access* to reflect `accountStatus`, which the summary does not carry.
 *    The only way to supply all three is one detail POST per row, which section 4.3's own callout
 *    forbids ("fifteen extra POSTs per page"). So the actions live on the detail screen, where the
 *    record is loaded and both statuses are known, and this is reported rather than papered over.
 */
export function EmployeeListScreen() {
  const session = useAuthenticatedSession();
  const isAccountant =
    session.role === UserRole.AccountantAdmin || session.role === UserRole.AccountantUser;

  // CustomersScreens.md section 4.4: *View employees* on a Customer's detail screen links to
  // /employees?customerId=... and the filter must arrive PRESET. Read once, as the initial value
  // only -- the picker owns the filter from then on, so changing it does not rewrite the URL and
  // going Back does not fight the control. A customerId outside the picker's first page of options
  // still filters correctly; the Select simply shows no selection, which is the 50-Customer cap
  // already reported and not a second bug.
  const [searchParams] = useSearchParams();
  // Rule C: an Accountant chooses a Customer; a Customer Admin's is the session's and is never a control.
  const [customerId, setCustomerId] = useState<string | null>(
    isAccountant ? searchParams.get('customerId') : session.customerId,
  );
  const [status, setStatus] = useState<EmployeeStatus | null>(null); // Rule B: no default.
  const [hasAccount, setHasAccount] = useState<boolean | null>(null);
  const [searchInput, setSearchInput] = useState('');
  const [searchTerm, setSearchTerm] = useState<string | null>(null);
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = useState(DEFAULT_PAGE_SIZE);

  const [registerOpen, setRegisterOpen] = useState(false);
  const [justRegistered, setJustRegistered] = useState<EmployeeDetail | null>(null);
  const [inviteTarget, setInviteTarget] = useState<EmployeeDetail | null>(null);
  const [invitedName, setInvitedName] = useState<string | null>(null);
  const [menuRow, setMenuRow] = useState<{ anchor: HTMLElement; row: EmployeeSummary } | null>(null);

  /**
   * Rule D. The 300 ms lives here rather than in a shared hook because no shared debounce exists and
   * this plan may not add one to `shared/` (its section 0.2 rule A); the search box on the Customer
   * picker does not need one, so this is the slice's only occurrence.
   */
  useEffect(() => {
    const trimmed = searchInput.trim();
    const timer = setTimeout(() => {
      // Rule E, and section 9.3 rule F: `null` for "no search", NEVER `''` -- the handler trims and
      // compares, so an empty string is a filter for the empty string.
      setSearchTerm(trimmed.length === 0 ? null : trimmed);
      setPageNumber(1);
    }, 300);
    return () => {
      clearTimeout(timer);
    };
  }, [searchInput]);

  /** Rule C. Active Customers only; the key matches the Customers slice's own so the page is fetched once. */
  const customerFilters = { status: 'Active' as const, search: null, pageNumber: 1, pageSize: 50 };
  const customers = useQuery({
    queryKey: ['customers', 'list', customerFilters],
    queryFn: () => listCustomers(customerFilters),
    enabled: isAccountant,
  });
  const customerOptions: readonly CustomerSummary[] = customers.data?.items ?? [];

  /**
   * Rule C: the query does not run for an Accountant who has chosen no Customer. `enabled` here is a
   * DATA dependency, not a permission -- the screen has nothing to label the rows with until a
   * Customer is named (GeneralUIArchitecture.md section 3.2 rule B).
   */
  const needsCustomerChoice = isAccountant && customerId === null;
  const employees = useEmployeeList(
    { customerId, status, hasAccount, searchTerm, pageNumber, pageSize },
    { enabled: !needsCustomerChoice },
  );

  const hasFilters =
    status !== null || hasAccount !== null || searchTerm !== null;

  const columns: readonly Column<EmployeeSummary>[] = [
    {
      key: 'name',
      header: 'Name',
      // Family name first, matching the server's ordering, so the column reads in the order it is sorted.
      render: (row) => (
        <Link component={RouterLink} to={`/employees/${row.id}`} underline="hover">
          {row.familyName}, {row.givenName}
        </Link>
      ),
    },
    { key: 'jobTitle', header: 'Job title', render: (row) => row.jobTitle ?? '—' },
    {
      key: 'employment',
      header: 'Employment',
      // EMPLOYMENT status, not access. The two vocabularies share the word "Active".
      render: (row) => <StatusChip status={row.status} />,
    },
    {
      key: 'role',
      header: 'Role',
      /**
       * `role` is a NULLABLE INTEGER and `AccountantAdmin` is `0`, which is falsy -- so this is an
       * explicit `=== null`, never `row.role ? … : …` and never `row.role || fallback`
       * (GeneralUIArchitecture.md section 10.1). `null` renders "Not invited", NEVER "Employee":
       * defaulting it would show every accountless person as holding a role they do not have.
       */
      render: (row) => (row.role === null ? 'Not invited' : ROLE_LABELS[row.role]),
    },
    {
      key: 'access',
      header: 'Access',
      /**
       * NEVER LABELLED "Active". `hasAccount: true` means an account EXISTS; it may be Invited or
       * Suspended, and this DTO has no `accountStatus` to say which. The detail screen's `Access:`
       * chip is the only place that answer lives.
       */
      render: (row) => (row.hasAccount ? 'Has account' : 'Not invited'),
    },
    {
      key: 'actions',
      header: '',
      align: 'right',
      width: 56,
      render: (row) => (
        <IconButton
          aria-label={`Actions for ${row.givenName} ${row.familyName}`}
          size="small"
          onClick={(event) => {
            setMenuRow({ anchor: event.currentTarget, row });
          }}
        >
          <MoreVertIcon fontSize="small" />
        </IconButton>
      ),
    },
  ];

  return (
    <Box>
      <PageHeader
        title="Employees"
        subtitle="Active and departed employees. Nothing here is ever deleted."
        action={
          can(session.role, 'RegisterEmployee') ? (
            <Button
              variant="contained"
              onClick={() => {
                setRegisterOpen(true);
              }}
            >
              Register Employee
            </Button>
          ) : undefined
        }
      />

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
          <TextField
            label="Search"
            value={searchInput}
            onChange={(event) => {
              // Rule D: the cap is enforced on input rather than waited for as a 422.
              setSearchInput(event.target.value.slice(0, SEARCH_TERM_MAX_LENGTH));
            }}
            helperText="Matches name and work email. Work email is not shown in the table."
            slotProps={{ htmlInput: { maxLength: SEARCH_TERM_MAX_LENGTH } }}
            sx={{ minWidth: 260 }}
          />

          {/* Rule C. Hidden for a CustomerAdmin -- not disabled, not drawn-and-ignored. */}
          {isAccountant && (
            <Autocomplete
              options={customerOptions}
              loading={customers.isLoading}
              getOptionLabel={(option) => option.legalName}
              isOptionEqualToValue={(option, value) => option.id === value.id}
              value={customerOptions.find((option) => option.id === customerId) ?? null}
              onChange={(_event, option) => {
                setCustomerId(option === null ? null : option.id);
                setPageNumber(1); // Rule E.
              }}
              sx={{ minWidth: 240 }}
              renderInput={(params) => (
                <TextField {...params} label="Customer" helperText="Required to list employees." />
              )}
            />
          )}

          <TextField
            select
            label="Employment"
            value={status ?? ''}
            onChange={(event) => {
              const value = event.target.value;
              // Rule B and section 4.5 rule C: `null` for "both", never `''` -- `""` is
              // 422 "Unknown employee status.", and so is "active".
              setStatus(value === '' ? null : (value as EmployeeStatus));
              setPageNumber(1); // Rule E.
            }}
            sx={{ minWidth: 160 }}
          >
            <MenuItem value="">Active and departed</MenuItem>
            <MenuItem value="Active">Active</MenuItem>
            <MenuItem value="Departed">Departed</MenuItem>
          </TextField>

          <TextField
            select
            label="Access"
            value={hasAccount === null ? '' : String(hasAccount)}
            onChange={(event) => {
              const value = event.target.value;
              setHasAccount(value === '' ? null : value === 'true');
              setPageNumber(1); // Rule E.
            }}
            sx={{ minWidth: 170 }}
          >
            <MenuItem value="">Any</MenuItem>
            <MenuItem value="true">Has an account</MenuItem>
            <MenuItem value="false">Not invited</MenuItem>
          </TextField>
        </Stack>
      </Paper>

      {needsCustomerChoice ? (
        /* Rule C. Not an error and not an empty table: there is nothing to identify the rows with. */
        <Paper variant="outlined">
          <EmptyState
            message="Choose a Customer"
            detail="An employee row does not carry its employer, so pick a Customer above to list its employees."
          />
        </Paper>
      ) : (
        <PaginatedTable
          data={employees.data}
          columns={columns}
          getRowKey={(row) => row.id}
          isLoading={employees.isLoading}
          isFetching={employees.isFetching}
          error={employees.error}
          onPageChange={setPageNumber}
          onPageSizeChange={(next) => {
            setPageSize(next);
            setPageNumber(1); // Rule E.
          }}
          isOverrunPage={employees.isOverrunPage}
          emptyMessage={hasFilters ? 'No employees match these filters' : 'No employees yet'}
          emptyDetail={
            hasFilters
              ? 'Clear the filters to see every employee, active and departed.'
              : undefined
          }
          emptyAction={
            hasFilters ? (
              <Button
                variant="outlined"
                onClick={() => {
                  setStatus(null);
                  setHasAccount(null);
                  setSearchInput('');
                  setPageNumber(1);
                }}
              >
                Clear filters
              </Button>
            ) : can(session.role, 'RegisterEmployee') ? (
              <Button
                variant="contained"
                onClick={() => {
                  setRegisterOpen(true);
                }}
              >
                Register Employee
              </Button>
            ) : undefined
          }
          ariaLabel="Employees"
        />
      )}

      {/* Rule G. One item, and it is a navigation. */}
      <Menu
        open={menuRow !== null}
        anchorEl={menuRow?.anchor ?? null}
        onClose={() => {
          setMenuRow(null);
        }}
      >
        <MenuList disablePadding>
          <MenuItem
            component={RouterLink}
            to={menuRow === null ? '/employees' : `/employees/${menuRow.row.id}`}
            onClick={() => {
              setMenuRow(null);
            }}
          >
            View
          </MenuItem>
        </MenuList>
      </Menu>

      {registerOpen && (
        <RegisterEmployeeDialog
          open={registerOpen}
          role={session.role}
          sessionCustomerId={session.customerId}
          onClose={() => {
            setRegisterOpen(false);
          }}
          onRegistered={(created) => {
            setRegisterOpen(false);
            setJustRegistered(created);
          }}
        />
      )}

      {/*
        Section 6.2, "Then what": *Invite* is offered in the SNACKBAR'S ACTION SLOT, after registration
        has definitely succeeded -- never as a checkbox on the register form, because there is no
        transaction spanning the two endpoints and a failed invite would leave a registered Employee
        behind an error that looks like nothing happened. The created record IS an EmployeeDetail, so
        the invite dialog opens with the work email already known and needs no second read.
      */}
      <Snackbar
        open={justRegistered !== null}
        autoHideDuration={10000}
        onClose={() => {
          setJustRegistered(null);
        }}
        message={
          justRegistered === null
            ? ''
            : `${justRegistered.givenName} ${justRegistered.familyName} registered. No account was created.`
        }
        action={
          /* Section 9.3 rule C: BLOCK the offer when there is no address on file --
             422 "No email address on file for this employee." The dialog's field is required, so the
             422 is unreachable through it, but offering *Invite* for somebody with no email invites
             the operator to invent an address at the moment of least information. */
          justRegistered !== null &&
          justRegistered.workEmail !== null &&
          can(session.role, 'InviteEmployee') ? (
            <Button
              color="secondary"
              size="small"
              onClick={() => {
                setInviteTarget(justRegistered);
                setJustRegistered(null);
              }}
            >
              Invite
            </Button>
          ) : undefined
        }
      />

      {inviteTarget !== null && (
        <InviteEmployeeDialog
          open
          employee={inviteTarget}
          onClose={() => {
            setInviteTarget(null);
          }}
          onInvited={(name) => {
            setInviteTarget(null);
            setInvitedName(name);
          }}
        />
      )}

      <Snackbar
        open={invitedName !== null}
        autoHideDuration={8000}
        onClose={() => {
          setInvitedName(null);
        }}
        message={invitedName === null ? '' : `Invitation sent to ${invitedName}.`}
      />
    </Box>
  );
}
