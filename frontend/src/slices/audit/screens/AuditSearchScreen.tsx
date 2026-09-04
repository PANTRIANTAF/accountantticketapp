import { useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';
import Alert from '@mui/material/Alert';
import Button from '@mui/material/Button';
import RefreshIcon from '@mui/icons-material/Refresh';
import { ApiError } from '../../../shared/api/http';
import { AccessDeniedPage } from '../../../shared/components/AccessDeniedPage';
import { PageHeader } from '../../../shared/components/PageHeader';
import { formatCount } from '../auditFormat';
import { AuditEntryTable } from '../components/AuditEntryTable';
import { AuditFilterPanel } from '../components/AuditFilterPanel';
import { useAuditSearch } from '../queries';
import type { AuditSearchRequest } from '../types';
import {
  activeFilters,
  auditFilterIssue,
  emptyAuditSearchRequest,
  parseAuditSearchParams,
  toAuditSearchParams,
  toSearchRequest,
  type AuditFilterField,
  type AuditFilterValues,
} from './auditFilterSchema';

/**
 * The audit log search screen. Route /audit, AccountantAdmin only. AuditScreens.md section 3.
 *
 * THE APPLIED FILTERS LIVE IN THE URL, AND THE URL IS THE SOURCE OF TRUTH (section 3.2 rule E). The
 * query key derives from what was parsed back out of the query string, so:
 *
 *   - Back from an entry restores the search instead of re-running an unfiltered one, which on a
 *     table of hundreds of thousands of rows is the difference between resuming an investigation and
 *     starting it again;
 *   - a search is a link one Admin can hand to another, which is how an investigation is handed over;
 *   - a reload keeps the filters, and the panel re-seeds itself from them.
 *
 * NO WRITE PATH EXISTS HERE, and none is disabled-but-present either. The audit log is append-only
 * (20260901_002_ReshapeAuditEntries.sql), so there is no export, no acknowledge, no annotate and no
 * delete: a greyed-out Export button is a promise the API cannot keep (section 3.6).
 */
export function AuditSearchScreen() {
  const [searchParams, setSearchParams] = useSearchParams();

  // Parsed from the URL on every render, so the browser's own history is the filter state.
  const applied = useMemo(() => parseAuditSearchParams(searchParams), [searchParams]);

  /**
   * A LINK CAN CARRY A FILTER SET THE SERVER WOULD REJECT -- hand-edited, mistyped, or truncated by a
   * chat client. Sending it anyway produces either a model-binding 400 (a non-GUID customerId never
   * reaches a handler) or a 422 whose sentence has no field to sit beside, and the reader is left
   * guessing which of eight inputs is wrong. So the request is not made and the reason is stated.
   *
   * THE FILTERS ARE NOT SILENTLY DROPPED to make the request valid. A search that quietly ignored the
   * bad filter would show whole-log rows under a filtered panel, and "this never happened" would be
   * concluded from rows that were merely excluded.
   */
  const issue = auditFilterIssue(applied);

  // `enabled` here is a DATA DEPENDENCY -- there is nothing valid to ask for yet -- and never a
  // permission gate (section 3.2 rule B). Permission is the route guard's job and the server's.
  const search = useAuditSearch(applied, { enabled: issue === null });

  const navigateTo = (request: AuditSearchRequest): void => {
    setSearchParams(toAuditSearchParams(request));
  };

  /** Applying any filter RESETS pageNumber TO 1 (section 3.2 rule F): page 9 of a new, narrower
   *  result set is usually empty, and an empty page reads as "nothing matched". */
  const handleApply = (values: AuditFilterValues): void => {
    navigateTo({ ...toSearchRequest(values), pageNumber: 1, pageSize: applied.pageSize });
  };

  const handleClearAll = (): void => {
    // The page size is not a filter and survives a clear.
    navigateTo({ ...emptyAuditSearchRequest(), pageSize: applied.pageSize });
  };

  const handleRemoveFilter = (field: AuditFilterField): void => {
    const next: AuditSearchRequest = { ...applied, pageNumber: 1 };
    next[field] = null;
    // Removing the kind removes the id with it: the pair without a kind is the 422 at
    // SearchAuditLogHandler.cs:92, and an id alone filters nothing meaningful.
    if (field === 'targetKind') next.targetId = null;
    navigateTo(next);
  };

  /**
   * A BARE 403 ON THE SEARCH IS THE WHOLE SCREEN'S ANSWER (section 3.4): the reader cannot read the
   * audit log at all, so a banner above an empty table would be a table that will never fill. Only
   * an AccountantAdmin holds ReadAuditLog (AuditActionCatalogue.cs:13), and RequireRole already keeps
   * everyone else out -- this covers the case where the server disagrees with the client's role map,
   * and the server is the one that decides.
   *
   * A 403 CARRYING `detail` never reaches here: that is the forced-password-change gate, which
   * http.ts intercepts. A 404 is not handled here at all -- the search endpoint cannot return one.
   */
  if (search.error instanceof ApiError && search.error.status === 403) {
    return <AccessDeniedPage />;
  }

  const chips = activeFilters(applied);
  const totalCount = search.data?.totalCount;

  return (
    <>
      <PageHeader
        title="Audit log"
        subtitle={
          totalCount === undefined
            ? 'Every recorded action, newest first.'
            : `${formatCount(totalCount)} ${totalCount === 1 ? 'entry' : 'entries'}, newest first.`
        }
        action={
          /* REFRESH IS EXPLICIT, AND THERE IS NO POLLING (section 3.3 rule C). An audit log that
             re-fetched on a timer would renumber pages under a reader mid-page and re-run a heavy
             query against the largest table in the database for a screen nobody is watching. */
          <Button
            startIcon={<RefreshIcon />}
            onClick={() => {
              void search.refetch();
            }}
            disabled={issue !== null || search.isFetching}
          >
            Refresh
          </Button>
        }
      />

      <AuditFilterPanel
        applied={applied}
        onApply={handleApply}
        onClearAll={handleClearAll}
        onRemoveFilter={handleRemoveFilter}
      />

      {issue !== null && (
        /* Not an ErrorBanner: nothing failed and nothing was requested. The filters stay on screen
           and stay editable, so the reader fixes the one field the sentence names. */
        <Alert severity="warning" sx={{ mb: 2 }}>
          {issue} This search has not been run.
        </Alert>
      )}

      <AuditEntryTable
        data={search.data}
        isLoading={search.isLoading}
        isFetching={search.isFetching}
        error={search.error}
        isOverrunPage={search.isOverrunPage}
        onPageChange={(pageNumber) => {
          navigateTo({ ...applied, pageNumber });
        }}
        onPageSizeChange={(pageSize) => {
          // A larger page invalidates the current page number, so it goes back to the first page.
          navigateTo({ ...applied, pageSize, pageNumber: 1 });
        }}
        /**
         * TWO DIFFERENT EMPTIES, NEVER THE SAME SENTENCE (section 3.5). "No results" for an over-run
         * page sends a reader away convinced the log has nothing, when the rows are on page 1 --
         * PaginatedTable's EmptyState handles that case itself from isOverrunPage, offering the way
         * back to the first page. What is left for this screen is the genuine zero-match case.
         */
        emptyMessage={
          issue !== null
            ? 'This link’s filters could not be searched.'
            : chips.length === 0
              ? 'The audit log has no entries.'
              : 'No entries match these filters.'
        }
        /**
         * THE EMPTY STATE NAMES THE FILTERS (section 3.5 rule C), because "no results" under a
         * collapsed panel is how a filtered table gets read as the whole log.
         *
         * The second sentence is the ONLY supported way to see a sequence of actions
         * (AuditScreens.md section 3.5 rule E, plan section 10 item 6): actorUserId plus a date
         * range. Nothing in the record links two rows -- there is no correlation id, no request id
         * and no trace id on AuditRecord, and no such filter on SearchAuditLogRequestDto
         * (punch-list item 22) -- so this text promises no filter that does not exist and no
         * timeline the UI would have to guess at.
         */
        emptyDetail={
          issue !== null
            ? 'Correct the filter above and search again.'
            : chips.length === 0
              ? undefined
              : `Active filters: ${chips.map((chip) => `${chip.label} = ${chip.value}`).join(', ')}. `
                + 'To follow a sequence of actions, filter by the actor and widen the date range.'
        }
        {...(chips.length === 0 || issue !== null
          ? {}
          : {
              emptyAction: (
                <Button onClick={handleClearAll} variant="outlined">
                  Clear all filters
                </Button>
              ),
            })}
      />
    </>
  );
}
