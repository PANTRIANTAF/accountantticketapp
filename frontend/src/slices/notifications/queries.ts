import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { DEFAULT_PAGE_SIZE } from '../../shared/api/paginated';
import { useSession } from '../../shared/auth/useSession';
import {
  usePaginatedQuery,
  type UsePaginatedQueryResult,
} from '../../shared/hooks/usePaginatedQuery';
import {
  getUnreadCount,
  listNotifications,
  markAllNotificationsRead,
  markNotificationsRead,
} from './api';
import type { MarkReadResult, Notification } from './types';

/**
 * Four hooks. Screens import hooks and never api.ts (GeneralUIArchitecture.md section 3.2 rule A).
 *
 * The two keys, per section 3.1 and NotificationsScreens.md section 4.2:
 *
 *   the list         ['notifications', 'list', { unreadOnly, pageNumber, pageSize }]
 *   the badge count  ['notifications', 'unreadCount']
 *
 * unreadOnly MUST be in the key: it changes the response, and two filters sharing one cache entry is
 * how a screen shows the wrong rows. It is React state on the screen, not a URL parameter -- a
 * bookmarkable ?unreadOnly=true is a saved link to a list that empties itself as the user reads.
 *
 * NO can() CALL ANYWHERE IN THIS SLICE. NotificationsActionCatalogue.cs:13-14 grants
 * ReadOwnNotifications and MarkOwnNotificationRead to all four roles and nothing else, so the check
 * could only ever pass. The two rows exist in shared/permissions/can.ts so the table matches the
 * server catalogue, not to be called from here (NotificationsScreens.md section 0).
 */

/** The list query key, in one place, so the mutations below invalidate exactly what they mean to. */
const LIST_KEY = ['notifications', 'list'] as const;

/** The unread-count key. GeneralUIArchitecture.md section 3.1 names this one explicitly. */
const UNREAD_COUNT_KEY = ['notifications', 'unreadCount'] as const;

/**
 * The paginated list. usePaginatedQuery clamps pageSize to MAX_PAGE_SIZE and pageNumber to >= 1, and
 * hands back isOverrunPage; it does not own the page number or the key, so both come from the screen
 * (GeneralUIArchitecture.md section 3.2 rule G, section 3.3).
 *
 * NO refetchInterval HERE. A list that repaginates under the reader every minute loses their place
 * (NotificationsScreens.md section 7 item 10).
 */
export function useNotifications(params: {
  unreadOnly: boolean;
  pageNumber: number;
  pageSize?: number;
}): UsePaginatedQueryResult<Notification> {
  const pageSize = params.pageSize ?? DEFAULT_PAGE_SIZE;

  return usePaginatedQuery<Notification>({
    queryKey: [...LIST_KEY, { unreadOnly: params.unreadOnly, pageNumber: params.pageNumber, pageSize }],
    queryFn: (page) =>
      listNotifications({
        unreadOnly: params.unreadOnly,
        pageNumber: page.pageNumber,
        pageSize: page.pageSize,
      }),
    pageNumber: params.pageNumber,
    pageSize,
  });
}

/**
 * THE ONLY POLLING QUERY IN THIS APPLICATION (GeneralUIArchitecture.md section 3.2 rule H).
 * `refetchInterval` appears in exactly one file and this is it; a second occurrence is a change to
 * that document, not a local decision.
 *
 * Polling is not a preference, it is the only option available, and that was verified rather than
 * assumed: the API has no SignalR package, no MapHub, no route emitting text/event-stream and no
 * websocket handling of any kind, so a client opening a socket connects to nothing and retries
 * forever. NotificationsScreens.md section 7 items 1-4 close the same door on EventSource, service
 * workers and the browser Notification API.
 *
 * Four options, all load-bearing (NotificationsScreens.md section 3.1):
 *
 * refetchInterval: 60_000
 *   Decided by that document; nothing upstream specifies an interval. ~480 requests per user per
 *   workday, each a SELECT COUNT(*) (GetUnreadCountHandler.cs:26-28). At ten seconds it is ~2,900,
 *   for a number no more useful, because nothing a user does with it is urgent.
 *
 * refetchIntervalInBackground: false
 *   Left at its default and STATED, because setting it true is the tempting mistake. aa_session is
 *   ExpireTimeSpan 8h with SlidingExpiration true (IdentityRegistration.cs:86-87), so every request
 *   resets the clock: a backgrounded tab polling each minute renews the cookie ~480 times a workday
 *   and the 8-hour expiry NEVER FIRES. An unattended machine in a shared office stays signed in
 *   overnight, and the only control the system has over abandoned sessions is disabled by a badge
 *   nobody is looking at. Invisible in development, where nobody leaves a tab open for eight hours.
 *
 * enabled: authenticated only
 *   The group has no .RequireAuthorization() and CurrentUserFactory answers 401
 *   (NotificationsEndpoints.cs:11-16), so an anonymous poll is not a 200 with zero -- it is a 401
 *   every 60 seconds, each one firing section 2.3 rule H's redirect to the page already on screen.
 *
 *   NotificationsScreens.md section 3.1 writes this gate as `const { session } = useSession()` and
 *   `enabled: session !== undefined`. THE SHIPPED HOOK HAS A DIFFERENT SHAPE: Phase 0's useSession()
 *   returns the Session union directly, discriminated on `status` with three cases
 *   (SessionProvider.tsx:50-53). The code wins over the sample; the intent -- disabled in BOTH
 *   non-authenticated states, `loading` as well as `anonymous` -- is what is implemented, and it
 *   answers the plan's own open question about whether the two are distinguishable. They are.
 *
 * staleTime: 30_000
 *   Half the interval, so a mount inside the window adds no request -- including the notification
 *   centre's heading, which reads this same cache entry rather than asking for a second number.
 *
 * A FAILED POLL RENDERS NOTHING. No banner, no toast: it fails on a 60-second timer, section 5.3
 * forbids a global error toast, and the failure surfaces where it is locatable -- as an ErrorBanner
 * on /notifications.
 */
export function useUnreadCount() {
  const session = useSession();

  return useQuery({
    queryKey: UNREAD_COUNT_KEY,
    queryFn: getUnreadCount,
    refetchInterval: 60_000,
    refetchIntervalInBackground: false,
    enabled: session.status === 'authenticated',
    staleTime: 30_000,
  });
}

/**
 * Mark specific notifications read. `retry: false` is inherited from Phase 0's queryClient
 * (queryClient.ts:25) and matters here: nothing in this API is idempotent, and mark-read can write
 * an audit row, so a retry can manufacture a second one.
 *
 * The variables are the id array from the CURRENT RENDER, never a getQueryData read of another key
 * and never a discarded page. MarkNotificationsReadHandler.cs:85-92 audits
 * PermissionDenied / Denied whenever fewer rows come back than ids were asked for -- AND STILL
 * RETURNS 200 (BACKEND_CHANGES_REQUIRED item 18). A stale id therefore manufactures a security event
 * against an innocent user with no visible symptom in the UI.
 *
 * NO onMutate AND NO OPTIMISTIC UPDATE (section 3.2 rule E, NotificationsScreens.md section 6
 * rule G). The badge lags the click by one round trip and that lag is accepted: decrementing by
 * notificationIds.length overcounts whenever a row was already read in another tab, leaving the badge
 * LOWER than the truth until the next poll, on the one number a user might act on.
 */
export function useMarkRead() {
  const queryClient = useQueryClient();

  return useMutation<MarkReadResult, Error, string[]>({
    mutationFn: markNotificationsRead,
    onSuccess: invalidateBoth(queryClient),
  });
}

/**
 * Mark every unread notification read. Takes no variables, because the endpoint takes no body.
 *
 * IRREVERSIBLE. There is no mark-unread endpoint anywhere in the API, so the screen gates this behind
 * a ConfirmDialog that names that consequence (NotificationsScreens.md section 6 rule F).
 */
export function useMarkAllRead() {
  const queryClient = useQueryClient();

  return useMutation<MarkReadResult, Error, void>({
    mutationFn: markAllNotificationsRead,
    onSuccess: invalidateBoth(queryClient),
  });
}

/**
 * BOTH KEYS, BY NAME, FROM BOTH MUTATIONS (section 3.2 rule C, NotificationsScreens.md section 6
 * rule G). The list because isRead changed on rows it holds; the count because the badge derives from
 * the server, not from this response.
 *
 * NO setQueryData. Rule D's seed-from-the-response pattern needs a response that IS the new state,
 * and { markedCount } is a tally, not a row -- this is the one slice where rule D does not apply.
 *
 * AND NOT invalidateQueries(['notifications']) ALONE, which also works and is worse: a broader blast
 * radius than the two keys that changed, and a reader cannot tell which caches the author believed
 * were affected.
 *
 * Invalidating only the list leaves the bell on its old number for up to 60 seconds beside a visibly
 * correct list, reported as "marking read does nothing". Invalidating only the count drops the badge
 * while the row keeps its dot, and the second click returns markedCount 0 -- "Nothing to mark read."
 * on a row that still looks unread.
 */
function invalidateBoth(queryClient: ReturnType<typeof useQueryClient>) {
  return () => {
    void queryClient.invalidateQueries({ queryKey: LIST_KEY });
    void queryClient.invalidateQueries({ queryKey: UNREAD_COUNT_KEY });
  };
}

/**
 * The snackbar copy for both mutations, built from the SERVER's markedCount
 * (NotificationsScreens.md section 6 rule D).
 *
 * Zero is reachable with no error at all: the handler counts only rows that were not already read
 * (MarkNotificationsReadHandler.cs:62-69), so a double click, or a second tab that got there first,
 * returns 200 with markedCount 0 -- and "0 notifications marked read" reads as a failure.
 *
 * It lives here, beside the mutations, rather than inside their onSuccess: Phase 0 ships no snackbar
 * provider, so the message is rendered by the screen that owns the Snackbar. The cache
 * reconciliation above is unconditional either way.
 */
export function markedReadMessage(markedCount: number): string {
  if (markedCount === 0) return 'Nothing to mark read.';
  if (markedCount === 1) return '1 notification marked read';
  return `${String(markedCount)} notifications marked read`;
}
