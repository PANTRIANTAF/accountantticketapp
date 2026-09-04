import { useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Checkbox from '@mui/material/Checkbox';
import FormControlLabel from '@mui/material/FormControlLabel';
import LinearProgress from '@mui/material/LinearProgress';
import List from '@mui/material/List';
import Paper from '@mui/material/Paper';
import Skeleton from '@mui/material/Skeleton';
import Snackbar from '@mui/material/Snackbar';
import TablePagination from '@mui/material/TablePagination';
import Typography from '@mui/material/Typography';
import { DEFAULT_PAGE_SIZE, MAX_PAGE_SIZE } from '../../../shared/api/paginated';
import { ConfirmDialog } from '../../../shared/components/ConfirmDialog';
import { EmptyState } from '../../../shared/components/EmptyState';
import { ErrorBanner } from '../../../shared/components/ErrorBanner';
import { PageHeader } from '../../../shared/components/PageHeader';
import { NotificationRow } from '../components/NotificationRow';
import { markedReadMessage, useMarkAllRead, useMarkRead, useNotifications, useUnreadCount } from '../queries';

/**
 * The notification centre at /notifications, registered for all four roles
 * (GeneralUIArchitecture.md section 4.1). NotificationsScreens.md sections 4 and 6.
 *
 * THE ONLY FEATURE ALL FOUR ROLES USE IDENTICALLY. NotificationsActionCatalogue.cs:13-14 grants both
 * actions to all four and 02-AuthorizationMatrix.md section 9 closes the other direction absolutely --
 * "Read another actor's notifications: Nobody, including Accountant Admins". No endpoint accepts a
 * recipient, so there is no row-scoping question, no two-DTO branch, no role-dependent column set, and
 * no can() call on this screen: the check could only ever pass.
 *
 * AND NO CLIENT-SIDE FILTERING FOR SECURITY, EVER. Every response is already scoped to CurrentUser.Id
 * by the handler (ListMyNotificationsHandler.cs:34, GetUnreadCountHandler.cs:27,
 * MarkNotificationsReadHandler.cs:59); a UI filtering rows here would be a UI concealing a server leak.
 *
 * THERE IS NO 403 AND NO 404 STATE HERE (section 4.3): every role may call every endpoint, and an
 * unowned id is filtered out with a 200 rather than refused. A 403 arriving would mean the server
 * catalogue changed and GeneralUIArchitecture.md section 6.1's table is stale -- fix the table, do not
 * catch the error.
 */
export function NotificationCentreScreen() {
  /**
   * Three pieces of React state, and `unreadOnly` is one of them rather than a URL parameter
   * (section 4.2): a bookmarkable ?unreadOnly=true is a saved link to a list that empties itself as
   * the user reads.
   */
  const [unreadOnly, setUnreadOnly] = useState(false);
  const [pageNumber, setPageNumber] = useState(1);
  const [pageSize, setPageSize] = useState<number>(DEFAULT_PAGE_SIZE);

  const [confirmAllOpen, setConfirmAllOpen] = useState(false);
  const [snackbar, setSnackbar] = useState<string | null>(null);

  const query = useNotifications({ unreadOnly, pageNumber, pageSize });

  /**
   * THE SAME CACHE ENTRY THE BELL READS -- ['notifications', 'unreadCount'] -- so the heading and the
   * badge are one request and one number that cannot drift (section 4.4 rule A). staleTime 30_000
   * means mounting this screen inside the window adds no request at all.
   *
   * The count is spoken here on arrival, because PageHeader moves focus to the h1 on route change
   * (section 8.4 item 3). That is the announcement section 3.2 rule G declines to make on a timer.
   */
  const unreadCount = useUnreadCount();

  const markRead = useMarkRead();
  const markAllRead = useMarkAllRead();

  const data = query.data;
  const items = data?.items ?? [];

  const showAll = () => {
    setUnreadOnly(false);
    setPageNumber(1);
  };

  /**
   * ONE ID, FROM THE CURRENT RENDER (section 6 rule E). Never a getQueryData read of another key and
   * never a discarded page: MarkNotificationsReadHandler.cs:85-92 audits PermissionDenied / Denied when
   * fewer rows come back than ids were asked for and STILL RETURNS 200
   * (BACKEND_CHANGES_REQUIRED item 18), so a stale id manufactures a security event against an innocent
   * user with no visible symptom here.
   *
   * The snackbar copy comes from the SERVER's markedCount, never from ids.length. The mutation's own
   * onSuccess has already invalidated both keys by the time this runs.
   */
  const handleMarkRead = (notificationId: string) => {
    markRead.mutate([notificationId], {
      onSuccess: ({ markedCount }) => {
        setSnackbar(markedReadMessage(markedCount));
      },
    });
  };

  const handleMarkAllRead = () => {
    markAllRead.mutate(undefined, {
      onSuccess: ({ markedCount }) => {
        setSnackbar(markedReadMessage(markedCount));
      },
      // Closed either way, so a failure's ErrorBanner is not hidden behind the dialog.
      onSettled: () => setConfirmAllOpen(false),
    });
  };

  return (
    <>
      <PageHeader
        title="Notifications"
        subtitle={unreadSubtitle(unreadCount.data?.unreadCount)}
        action={
          <Button
            variant="contained"
            onClick={() => setConfirmAllOpen(true)}
            disabled={markAllRead.isPending}
          >
            Mark all as read
          </Button>
        }
      />

      <FormControlLabel
        control={
          <Checkbox
            checked={unreadOnly}
            onChange={(event) => {
              setUnreadOnly(event.target.checked);
              // Page 1, always. Toggling the filter changes the size of the result set, so keeping
              // the cursor on page 3 lands the reader on an over-run page for no reason.
              setPageNumber(1);
            }}
          />
        }
        label="Unread only"
        sx={{ mb: 1 }}
      />

      {/* A failed mutation is a row action, so its banner sits ABOVE the list and the rows survive
          (section 7.2). The list's own failure is rendered inside the Paper below, in place of the
          rows. Both go through ErrorBanner, which owns section 7.1's taxonomy -- this screen never
          branches on a status itself. */}
      <ErrorBanner error={markRead.error ?? markAllRead.error} />

      <Paper variant="outlined">
        {/* A REFETCH KEEPS THE ROWS (section 4.3, section 7.4): subtle progress, never a skeleton. A
            skeleton on refetch blanks a list somebody is reading. */}
        <Box sx={{ height: 4 }}>
          {query.isFetching && !query.isLoading && <LinearProgress />}
        </Box>

        {query.isError ? (
          <Box sx={{ px: 2, pb: 2 }}>
            <ErrorBanner error={query.error} />
          </Box>
        ) : query.isLoading ? (
          <Box sx={{ p: 2 }}>
            {Array.from({ length: 5 }, (_, index) => (
              <Skeleton key={`skeleton-${String(index)}`} variant="text" height={48} />
            ))}
          </Box>
        ) : items.length === 0 ? (
          /**
           * Three different empty renderings, and telling them apart is the whole point.
           *
           * OVER-RUN PAGE (totalCount > 0 && items.length === 0) is genuinely reachable here, not a
           * theoretical case: marking read with *Unread only* on shrinks the result under the cursor.
           * EmptyState turns the flag into "Back to the first page" rather than "no results", which
           * would tell a user with 60 rows that they have none.
           *
           * NOTHING UNREAD offers *Show all*; NO NOTIFICATIONS YET offers nothing, because nothing a
           * user does creates one.
           */
          <EmptyState
            message={unreadOnly ? 'Nothing unread.' : 'No notifications yet.'}
            isOverrunPage={query.isOverrunPage}
            onBackToFirstPage={() => setPageNumber(1)}
            {...(unreadOnly && !query.isOverrunPage
              ? { action: <Button variant="outlined" onClick={showAll}>Show all</Button> }
              : {})}
          />
        ) : (
          /**
           * ORDERING IS THE SERVER'S -- createdAt desc then id desc
           * (ListMyNotificationsHandler.cs:44-45) -- read and unread interleaved, NOT unread-first,
           * and never re-sorted client-side: the server paginates, so sorting reorders 15 rows out of
           * 63 and produces an order that changes per page.
           */
          <List disablePadding aria-label="Notifications">
            {items.map((notification) => (
              <NotificationRow
                key={notification.id}
                notification={notification}
                onMarkRead={handleMarkRead}
                isMarkingRead={
                  markRead.isPending && (markRead.variables?.includes(notification.id) ?? false)
                }
              />
            ))}
          </List>
        )}

        {data !== undefined && (
          /**
           * THE PAGER, AND THE ONE THING THIS FILE HAD TO DUPLICATE.
           *
           * `page` and `onPageChange` below are the 1-based/0-based conversion that
           * GeneralUIArchitecture.md section 3.3 item 3 requires to exist in EXACTLY ONE PLACE --
           * shared/components/PaginatedTable.tsx, which owns it. NotificationsScreens.md section 4.1
           * says this screen "imports PaginatedTable's 1-based/0-based conversion rather than
           * re-deriving it", but PHASE 0 DOES NOT EXPORT IT: PaginatedTable.tsx:170 and :176 hold the
           * two arithmetic operations inline inside its own JSX, and this slice may not edit anything
           * under shared/. The same document requires a List here and not a PaginatedTable, so
           * reusing that component instead is not available either.
           *
           * So this is a SECOND copy, and it is reported as a Phase 0 gap rather than hidden: the fix
           * is for PaginatedTable to export the conversion (or a small Pager component) and for these
           * two lines to call it.
           *
           * rowsPerPage COMES FROM data.pageSize, NEVER from the pageSize state above.
           * PaginatedQuery.Normalize clamps to 50 and answers 200 (BACKEND_CHANGES_REQUIRED item 17):
           * ask for 200 and you get 50, and a pager rendered from the request computes every page
           * boundary wrong from then on, with no error anywhere to explain it.
           */
          <TablePagination
            component="div"
            count={data.totalCount}
            page={Math.max(data.pageNumber - 1, 0)}
            rowsPerPage={data.pageSize}
            rowsPerPageOptions={[10, 15, 25, 50].filter((size) => size <= MAX_PAGE_SIZE)}
            onPageChange={(_event, page) => setPageNumber(page + 1)}
            onRowsPerPageChange={(event) => {
              setPageSize(Number(event.target.value));
              setPageNumber(1);
            }}
          />
        )}
      </Paper>

      {/**
       * MARK ALL AS READ IS IRREVERSIBLE AND THE DIALOG SAYS SO (section 6 rule F). There is no
       * mark-unread endpoint anywhere in the API and `is_read` is the row's only mutable field, so
       * unread state once cleared cannot be restored by any operation this application offers. The copy
       * names that consequence instead of asking "are you sure?", which is a click people learn to make
       * without reading.
       *
       * A single row's *Mark as read* gets no dialog: one visible row, and the same rule would put a
       * modal in front of every click.
       */}
      <ConfirmDialog
        open={confirmAllOpen}
        title="Mark all notifications as read?"
        confirmLabel="Mark all as read"
        isPending={markAllRead.isPending}
        onConfirm={handleMarkAllRead}
        onClose={() => setConfirmAllOpen(false)}
      >
        <Typography variant="body2">
          This cannot be undone; there is no way to mark a notification unread again.
        </Typography>
      </ConfirmDialog>

      {/* Successes are the only toasts (section 7.2, last row). The wording is built from the server's
          markedCount, so "Nothing to mark read." is what a double click or a second tab produces --
          not "0 notifications marked read", which reads as a failure. */}
      <Snackbar
        open={snackbar !== null}
        message={snackbar ?? ''}
        autoHideDuration={5000}
        onClose={() => setSnackbar(null)}
      />
    </>
  );
}

/**
 * "3 unread" under the title. Undefined while the count is loading or after a failed poll -- the
 * heading simply carries no subtitle, because a failed unread-count is not this screen's error to
 * announce twice (section 3.2 rule C).
 */
function unreadSubtitle(unreadCount: number | undefined): string | undefined {
  if (unreadCount === undefined) return undefined;
  if (unreadCount === 0) return 'Nothing unread';
  if (unreadCount === 1) return '1 unread';
  return `${String(unreadCount)} unread`;
}
