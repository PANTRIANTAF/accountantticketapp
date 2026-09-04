import { useLayoutEffect, useRef, useState } from 'react';
import Box from '@mui/material/Box';
import Button from '@mui/material/Button';
import Chip from '@mui/material/Chip';
import Link from '@mui/material/Link';
import ListItem from '@mui/material/ListItem';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord';
import { Link as RouterLink } from 'react-router-dom';
import { formatDateTime } from '../../../shared/format/dates';
import {
  EVENT_LABELS,
  destinationFor,
  isTicketEventKind,
  TICKET_UNAVAILABLE_NOTE,
} from '../eventKinds';
import type { Notification } from '../types';

/**
 * One row of the notification centre. NotificationsScreens.md sections 4.1 and 4.4.
 *
 * A ListItem, not a table row: a notification is a kind label, a title, a sentence and a timestamp,
 * one of which wraps -- as a table it is three columns with one at 80% width and a header labelling
 * nothing (section 4.1).
 *
 * THREE RULES WITH CONSEQUENCES BEYOND TIDINESS:
 *
 * A. `title` and `body` RENDER VERBATIM (rule B), never paraphrased, prefixed or templated over. The
 *    producing handler wrote them for this reader; the kind chip is a CATEGORY, not a replacement for
 *    the title.
 *
 * B. `body` IS SERVER-SUPPLIED TEXT, RENDERED AS TEXT (rule C). Never dangerouslySetInnerHTML, never
 *    a markdown renderer, never innerHTML. Producers write `\n` and not markup, so the newlines are
 *    honoured with whiteSpace: 'pre-line'. Every producer today writes a server-side literal; the
 *    moment this system gains user-authored ticket comments a body is attacker-influencable, and this
 *    rule is all that stands between that and stored XSS.
 *
 * C. `readAt`, `emailStatus` AND `ticketId` ARE NEVER RENDERED (rule F). The dot already says what
 *    readAt says; emailStatus is operator telemetry a recipient can only worry about; a raw ticketId
 *    GUID is a value with no destination. `createdAt` goes through shared/format/dates.ts -- it
 *    carries an offset and would parse directly, but it still goes through the one module, because
 *    that is where a timezone bug gets fixed once instead of in six screens.
 */
export function NotificationRow({
  notification,
  onMarkRead,
  isMarkingRead = false,
}: {
  notification: Notification;
  /** Receives THIS row's single id. Never a batch, and never an id from the query cache. */
  onMarkRead: (notificationId: string) => void;
  isMarkingRead?: boolean;
}) {
  const [expanded, setExpanded] = useState(false);
  const [isTruncated, setIsTruncated] = useState(false);
  const bodyRef = useRef<HTMLDivElement | null>(null);

  /**
   * Whether the two-line clamp actually hid anything, measured rather than guessed from the string
   * length -- a 200-character body may be two lines on a wide screen and four on a narrow one. Only
   * measured while collapsed: expanded, scrollHeight equals clientHeight, and re-measuring would
   * remove the control the user just pressed.
   */
  useLayoutEffect(() => {
    const element = bodyRef.current;
    if (element === null || expanded) return;
    setIsTruncated(element.scrollHeight > element.clientHeight + 1);
  }, [notification.body, expanded]);

  /**
   * The label resolution of section 5 rule D, and the `?? eventKind` is the whole of it.
   *
   * AN UNRECOGNISED KIND RENDERS, VISIBLY PLAIN, AND NEVER DISAPPEARS: the raw kind string as the
   * label, then the server's title and body, which are always present. No exhaustive switch, no
   * `throw new Error('unknown kind')`, no assertNever, and NEVER A FILTERED-OUT ROW. A hidden
   * notification is worse than an ugly one -- the badge says 3, the list shows 2, the user can neither
   * find nor clear the third, the badge sticks at 1 forever, and the only recovery is mark-all-read,
   * which is irreversible. NotificationEvents.cs will grow and EVENT_LABELS will lag it by at least a
   * commit. Render the ugly row.
   */
  const mappedLabel = EVENT_LABELS[notification.eventKind];
  const label = mappedLabel ?? notification.eventKind;

  /**
   * null for every kind today, and the row is therefore NON-INTERACTIVE: no anchor, no Link, no
   * onClick, no pointer cursor, no disabled-looking link (section 5 rules A and C). The branch exists
   * so that the day Screens/TicketsScreens.md names a route, eventKinds.ts is the only file that
   * changes.
   */
  const destination = destinationFor(notification);

  /**
   * The muted line the twelve ticket kinds carry, and only while they have nowhere to go. It
   * disappears by itself when destinationFor starts answering.
   */
  const showTicketNote = destination === null && isTicketEventKind(notification.eventKind);

  return (
    <ListItem divider alignItems="flex-start" sx={{ gap: 1.5, py: 1.5 }}>
      {/* THE UNREAD MARKER IS A FILLED DOT *AND* A BOLDER TITLE below: colour is never the only
          carrier of meaning (GeneralUIArchitecture.md section 8.4). titleAccess gives the dot an
          accessible name, so "Unread" is not a visual-only signal either. The Box keeps the text
          block aligned whether or not the dot is drawn. */}
      <Box sx={{ width: 16, flexShrink: 0, pt: 0.75 }}>
        {!notification.isRead && (
          <FiberManualRecordIcon color="primary" titleAccess="Unread" sx={{ fontSize: 12 }} />
        )}
      </Box>

      <Box sx={{ minWidth: 0, flexGrow: 1 }}>
        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1}
          sx={{ alignItems: { sm: 'baseline' }, mb: 0.5 }}
        >
          {/* A mapped kind gets the filled category chip; an unmapped one gets its raw string in an
              outlined chip -- readable, obviously untranslated, and never hidden. */}
          <Chip
            size="small"
            label={label}
            variant={mappedLabel === undefined ? 'outlined' : 'filled'}
            sx={{ flexShrink: 0 }}
          />

          <Typography
            variant="body1"
            component="p"
            // The second half of the unread marker. Weights come from the theme's typography tokens,
            // not from a literal.
            sx={{
              flexGrow: 1,
              minWidth: 0,
              fontWeight: notification.isRead ? 'fontWeightRegular' : 'fontWeightBold',
            }}
          >
            {destination === null ? (
              notification.title
            ) : (
              <Link component={RouterLink} to={destination}>
                {notification.title}
              </Link>
            )}
          </Typography>

          <Typography variant="caption" color="text.secondary" sx={{ flexShrink: 0 }}>
            {formatDateTime(notification.createdAt)}
          </Typography>
        </Stack>

        {/* Rule C and rule D: the server's text, as text, with its newlines, clamped to two lines
            until the reader asks for the rest. The column is TEXT with no ceiling
            (Core/Notification.cs), so one 4,000-character body would otherwise push every other row
            off the screen. */}
        <Typography
          ref={bodyRef}
          variant="body2"
          component="div"
          color="text.secondary"
          sx={{
            whiteSpace: 'pre-line',
            overflowWrap: 'anywhere',
            ...(expanded
              ? {}
              : {
                  display: '-webkit-box',
                  WebkitBoxOrient: 'vertical',
                  WebkitLineClamp: 2,
                  overflow: 'hidden',
                }),
          }}
        >
          {notification.body}
        </Typography>

        {(isTruncated || expanded) && (
          <Button
            size="small"
            variant="text"
            onClick={() => setExpanded((previous) => !previous)}
            sx={{ px: 0, minWidth: 0 }}
          >
            {expanded ? 'Show less' : 'Show more'}
          </Button>
        )}

        {showTicketNote && (
          <Typography variant="caption" component="p" color="text.disabled" sx={{ mt: 0.5 }}>
            {TICKET_UNAVAILABLE_NOTE}
          </Typography>
        )}

        {/* Exactly one id, from THIS render, and no ConfirmDialog: one visible row, and the same rule
            would put a modal in front of every click (section 6 rule F). The button disables while
            its mutation is in flight (section 7.4). */}
        {!notification.isRead && (
          <Box sx={{ mt: 0.5 }}>
            <Button
              size="small"
              variant="outlined"
              disabled={isMarkingRead}
              onClick={() => onMarkRead(notification.id)}
            >
              Mark as read
            </Button>
          </Box>
        )}
      </Box>
    </ListItem>
  );
}
