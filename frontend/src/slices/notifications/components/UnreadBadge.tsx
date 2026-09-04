import Badge from '@mui/material/Badge';
import IconButton from '@mui/material/IconButton';
import NotificationsIcon from '@mui/icons-material/Notifications';
import { Link as RouterLink } from 'react-router-dom';
import { useUnreadCount } from '../queries';

/**
 * The unread bell in the AppBar. NotificationsScreens.md section 3, and it sits where
 * GeneralUIArchitecture.md section 5.1's diagram draws `[bell 3]`, left of the account menu.
 *
 * IT READS THE COUNT AND NOTHING ELSE. It never lists, never marks anything read, and never renders
 * an error.
 *
 * HOW IT GETS MOUNTED, AND WHY NOT BY AN IMPORT. shared/ may never import from slices/
 * (GeneralUIArchitecture.md section 1.4 rule A), so this component cannot be imported into
 * shared/components/AppShell.tsx -- that would be the app's ONLY rule-A violation and it would drag
 * this slice's api.ts, types.ts and queries.ts into the entry chunk of every screen including /login,
 * which draws no shell. Instead AppShell takes `notificationSlot?: ReactNode` (AppShell.tsx:75) and
 * routes.tsx -- the one file whose job is already importing every slice (rule E) -- passes
 * <AppShell notificationSlot={<UnreadBadge />} />. With the prop omitted the shell simply draws no
 * bell, so Phase 0 stays runnable on its own.
 *
 * Four ways this component goes wrong, all of them avoided above or below:
 *   1. Importing it into AppShell (the rule-A violation).
 *   2. Polling while anonymous -- a 401 every 60 seconds, each firing a redirect to the page already
 *      on screen. The `enabled` gate is in useUnreadCount.
 *   3. Turning background polling on "to keep the badge fresh" -- it keeps the SESSION fresh,
 *      forever. That option is set, and explained, in useUnreadCount. It is named nowhere else in
 *      this slice, so the grep of NotificationsScreens.md success criterion 3 returns one file.
 *   4. A banner or toast on a failed poll. See below.
 */
export function UnreadBadge() {
  // No `error` and no `isError` read on purpose. A FAILED POLL RENDERS THE BELL WITH NO BADGE AND NO
  // ERROR (section 3.2 rule C): it fails on a 60-second timer, and section 5.3 forbids a global error
  // toast -- one banner a minute for a number the user never asked for. The failure surfaces where it
  // is locatable, as an ErrorBanner on /notifications.
  const { data } = useUnreadCount();

  // data.unreadCount, NEVER a cached page's length (section 3.2 rule A). A page holds at most 50
  // rows, so a user with 63 unread would see "50", and the number would change as they paged.
  const count = data?.unreadCount ?? 0;

  return (
    <IconButton
      component={RouterLink}
      to="/notifications"
      color="inherit"
      /**
       * THE ARIA-LABEL CARRIES THE COUNT (section 3.2 rule F, section 8.4 item 4). The button is
       * icon-only and the badge is its entire information content, so without this a screen-reader
       * user hears "Notifications, button" and the number is a visual-only signal.
       *
       * There is deliberately NO aria-live anywhere here (rule G): a polite region on a value
       * repolled every 60 seconds interrupts a screen-reader user mid-sentence, on a schedule, with a
       * number they cannot act on from where they are standing. It is announced on focus by this
       * label, and again in the page heading on arrival.
       */
      aria-label={count > 0 ? `Notifications, ${String(count)} unread` : 'Notifications, none unread'}
    >
      {/* `invisible` at zero (rule B): MUI's Badge shows the zero by default, and a permanent grey
          "0" beside a bell trains everyone to stop looking at it. */}
      <Badge badgeContent={count} color="error" invisible={count === 0}>
        <NotificationsIcon />
      </Badge>
    </IconButton>
  );
}
